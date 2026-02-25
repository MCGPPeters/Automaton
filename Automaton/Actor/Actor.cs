// =============================================================================
// Actor Runtime
// =============================================================================
// The Actor Model as a specialization of the Automaton kernel.
//
// Each actor is an automaton with a mailbox: messages arrive asynchronously,
// are processed sequentially, and transitions may produce effects that send
// messages to other actors or spawn new ones.
//
// Structurally, an Actor is the shared AutomatonRuntime with:
// - Observer = no-op (actor state is internal)
// - Interpreter = execute effect with self-reference, no feedback events
//
// Historical lineage:
//     Mealy Machine (1955) → Actor Model (Hewitt, 1973) → Erlang/OTP (1986)
//     → Akka (2009) → Orleans (2014) → Automaton (2025)
// =============================================================================

using System.Threading.Channels;

namespace Automaton.Actor;

/// <summary>
/// A typed reference to an actor. Used to send messages without exposing internals.
/// </summary>
/// <typeparam name="TEvent">The type of messages this actor accepts.</typeparam>
public sealed class ActorRef<TEvent>
{
    private readonly ChannelWriter<TEvent> _writer;
    internal int _sentCount;

    /// <summary>
    /// The unique name of the actor.
    /// </summary>
    public string Name { get; }

    internal ActorRef(string name, ChannelWriter<TEvent> writer)
    {
        Name = name;
        _writer = writer;
    }

    /// <summary>
    /// Sends a message to the actor's mailbox (fire-and-forget, tell pattern).
    /// </summary>
    public async ValueTask Tell(TEvent message)
    {
        Interlocked.Increment(ref _sentCount);
        await _writer.WriteAsync(message);
    }
}

/// <summary>
/// A running actor instance with a mailbox and processing loop.
/// </summary>
/// <remarks>
/// <para>
/// Internally delegates to <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect}"/>
/// with a no-op observer and an interpreter that wraps the effect handler.
/// </para>
/// <para>
/// Each actor processes messages sequentially from its mailbox channel,
/// running the automaton's transition function for each message.
/// Effects are handled via an optional callback.
/// </para>
/// <example>
/// <code>
/// var actor = ActorInstance&lt;Counter, CounterState, CounterEvent, CounterEffect&gt;
///     .Spawn("counter-1");
///
/// await actor.Ref.Tell(new CounterEvent.Increment());
/// await actor.DrainMailbox();
/// // actor.State.Count == 1
/// </code>
/// </example>
/// </remarks>
public sealed class ActorInstance<TAutomaton, TState, TEvent, TEffect>
    where TAutomaton : Automaton<TState, TEvent, TEffect>
{
    private readonly AutomatonRuntime<TAutomaton, TState, TEvent, TEffect> _core;
    private readonly Channel<TEvent> _mailbox;
    private readonly CancellationTokenSource _cts = new();
    private int _processedCount;

    /// <summary>
    /// The current state of the actor.
    /// </summary>
    public TState State => _core.State;

    /// <summary>
    /// A reference to send messages to this actor.
    /// </summary>
    public ActorRef<TEvent> Ref { get; }

    /// <summary>
    /// All messages processed by this actor.
    /// </summary>
    public IReadOnlyList<TEvent> ProcessedMessages => _core.Events;

    private ActorInstance(
        AutomatonRuntime<TAutomaton, TState, TEvent, TEffect> core,
        Channel<TEvent> mailbox,
        ActorRef<TEvent> actorRef)
    {
        _core = core;
        _mailbox = mailbox;
        Ref = actorRef;
    }

    /// <summary>
    /// Spawns a new actor with the given name, starting its processing loop.
    /// </summary>
    public static ActorInstance<TAutomaton, TState, TEvent, TEffect> Spawn(
        string name,
        Func<TEffect, ActorRef<TEvent>, Task>? effectHandler = null)
    {
        var (state, _) = TAutomaton.Init();
        var mailbox = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var actorRef = new ActorRef<TEvent>(name, mailbox.Writer);

        // Observer: no-op — actor state is internal
        Observer<TState, TEvent, TEffect> observer = (_, _, _) => Task.CompletedTask;

        // Interpreter: wraps the effect handler with self-reference
        Interpreter<TEffect, TEvent> interpreter = effectHandler is not null
            ? async effect =>
            {
                await effectHandler(effect, actorRef);
                return [];
            }
        : _ => Task.FromResult<IEnumerable<TEvent>>([]);

        var core = new AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>(
            state, observer, interpreter);

        var actor = new ActorInstance<TAutomaton, TState, TEvent, TEffect>(
            core, mailbox, actorRef);

        // Start the processing loop
        _ = actor.ProcessLoop();

        return actor;
    }

    /// <summary>
    /// Stops the actor's processing loop gracefully.
    /// </summary>
    public async Task Stop()
    {
        _mailbox.Writer.Complete();
        await _cts.CancelAsync();
    }

    /// <summary>
    /// Waits until the mailbox is drained (all sent messages have been processed).
    /// </summary>
    /// <remarks>
    /// Uses a counter-based approach: each <see cref="ActorRef{TEvent}.Tell"/> increments
    /// a sent counter, and each completed dispatch increments a processed counter.
    /// This avoids the race condition where the channel appears empty (message read)
    /// but dispatch is still in-flight.
    /// </remarks>
    public async Task DrainMailbox()
    {
        var sent = Volatile.Read(ref Ref._sentCount);
        while (Volatile.Read(ref _processedCount) < sent)
        {
            await Task.Yield();
        }
    }

    private async Task ProcessLoop()
    {
        try
        {
            await foreach (var message in _mailbox.Reader.ReadAllAsync(_cts.Token))
            {
                await _core.Dispatch(message);
                Interlocked.Increment(ref _processedCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
    }
}
