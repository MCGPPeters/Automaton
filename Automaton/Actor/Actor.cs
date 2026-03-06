// =============================================================================
// Actor — Hewitt's Three Axioms on the Automaton Kernel
// =============================================================================
// Carl Hewitt's 1973 Actor Model defines exactly three things an actor can do
// in response to a message:
//
//     1. Send a finite number of messages to other actors
//     2. Create a finite number of new actors
//     3. Designate the behavior to be used for the next message
//
// That's it. No supervision. No ask pattern. No hierarchical naming.
// No Props, no Context, no lifecycle hooks. Those are Erlang (1986) and
// Akka (2009) additions — useful engineering, but not the model itself.
//
// This implementation maps Hewitt's axioms onto the Automaton kernel:
//
//     Axiom 1 (send):     Address<TCommand>.Tell()
//     Axiom 2 (create):   Actor.Spawn() returns Address<TCommand>
//     Axiom 3 (behavior): Decider.Decide + Automaton.Transition
//
// The key insight is that "designate behavior for next message" is exactly
// what a Mealy machine does: Transition(state, event) → (state', effect).
// The new state IS the new behavior. This is why Hewitt's actors and
// finite-state machines are the same mathematical object — both are
// coalgebras over the functor F(X) = (Output × X)^Input.
//
// All actors in this system are Deciders: they validate commands against
// state (Decide), produce events, and evolve (Transition). This is a
// domain choice — we model business logic, not raw message pipes.
//
// References:
//   Hewitt, Bishop, Steiger (1973). "A Universal Modular Actor Formalism
//   for Artificial Intelligence." IJCAI'73.
//
//   Hewitt (2010). "Actor Model of Computation: Scalable Robust Information
//   Systems." arXiv:1008.1459.
//
//   Chassaing, Jérémie (2021). "The Decider Pattern."
// =============================================================================

using System.Diagnostics;
using System.Threading.Channels;

namespace Automaton.Actor;

/// <summary>
/// Factory for spawning actors — Hewitt's axiom #2: "create a finite number of new actors."
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="Spawn{TDecider,TState,TCommand,TEvent,TEffect,TError,TParameters}"/>
/// creates a new actor with:
/// <list type="bullet">
///   <item>A bounded mailbox (channel) for incoming commands</item>
///   <item>A <see cref="DecidingRuntime{TDecider,TState,TCommand,TEvent,TEffect,TError,TParameters}"/>
///         with <c>threadSafe: false</c> — the processing loop serializes access</item>
///   <item>A background task that reads commands and passes them to the runtime</item>
///   <item>An opaque <see cref="Address{TCommand}"/> — the only way to communicate with the actor</item>
/// </list>
/// </para>
/// <para>
/// The actor's processing loop embodies Hewitt's fundamental guarantee: an actor
/// processes exactly one message at a time. There is no concurrency within an
/// actor — the mailbox serializes all access. This is why the runtime uses
/// <c>threadSafe: false</c>: the single reader provides the serialization.
/// </para>
/// </remarks>
public static class Actor
{
    /// <summary>
    /// Default mailbox capacity. Bounded channels provide backpressure when the
    /// actor falls behind — the sender's <see cref="Address{TCommand}.Tell"/> awaits
    /// until space is available.
    /// </summary>
    /// <remarks>
    /// 100 is a sensible default for domain actors processing business commands.
    /// It is large enough to absorb small bursts without blocking callers, but
    /// small enough to surface sustained overload as backpressure rather than
    /// unbounded memory growth.
    /// </remarks>
    public const int DefaultMailboxCapacity = 100;

    /// <summary>
    /// Spawns a new actor, returning its address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is Hewitt's axiom #2: the ability to create new actors at runtime.
    /// The returned <see cref="Address{TCommand}"/> is an unforgeable capability —
    /// the only way to communicate with the new actor.
    /// </para>
    /// <para>
    /// The actor's behavior is defined by the <typeparamref name="TDecider"/> type:
    /// <list type="bullet">
    ///   <item><c>Decide(state, command)</c> — validates commands, produces events</item>
    ///   <item><c>Transition(state, event)</c> — evolves state (designates next behavior)</item>
    ///   <item><c>Initialize(parameters)</c> — provides the initial state</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="parameters">Initialization parameters for the Decider.</param>
    /// <param name="observer">Observer notified after each state transition. Use a no-op observer for encapsulated actors.</param>
    /// <param name="interpreter">Interpreter that converts effects to feedback events. Use a no-op interpreter for actors without side effects.</param>
    /// <param name="mailboxCapacity">Bounded mailbox size. Defaults to <see cref="DefaultMailboxCapacity"/>.</param>
    /// <param name="cancellationToken">Token to stop the actor's processing loop.</param>
    /// <typeparam name="TDecider">The Decider type that defines the actor's behavior.</typeparam>
    /// <typeparam name="TState">The actor's internal state type.</typeparam>
    /// <typeparam name="TCommand">The command type this actor accepts.</typeparam>
    /// <typeparam name="TEvent">The event type produced by this actor's decisions.</typeparam>
    /// <typeparam name="TEffect">The effect type produced by state transitions.</typeparam>
    /// <typeparam name="TError">The error type for rejected commands.</typeparam>
    /// <typeparam name="TParameters">Initialization parameters type. Use <see cref="Unit"/> for parameterless actors.</typeparam>
    /// <returns>
    /// An <see cref="Address{TCommand}"/> — the unforgeable capability to send commands to the actor.
    /// </returns>
    public static async ValueTask<Address<TCommand>> Spawn<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>(
        TParameters parameters,
        Observer<TState, TEvent, TEffect> observer,
        Interpreter<TEffect, TEvent> interpreter,
        int mailboxCapacity = DefaultMailboxCapacity,
        CancellationToken cancellationToken = default)
        where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
    {
        using var spawnActivity = AutomatonDiagnostics.Source.StartActivity("Actor.Spawn");
        spawnActivity?.SetTag("actor.type", typeof(TDecider).Name);
        spawnActivity?.SetTag("actor.mailbox.capacity", mailboxCapacity);

        var channel = Channel.CreateBounded<TCommand>(
            new BoundedChannelOptions(mailboxCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        // DecidingRuntime with threadSafe: false — the single reader loop serializes access.
        // trackEvents: false — actor state is encapsulated; callers observe via the Observer delegate.
        var runtime = await DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>
            .Start(parameters, observer, interpreter, threadSafe: false, trackEvents: false, cancellationToken)
            .ConfigureAwait(false);

        var address = new Address<TCommand>(channel.Writer);

        // Fire-and-forget processing loop. The Task is not surfaced — Hewitt actors
        // are autonomous entities. The CancellationToken provides cooperative shutdown.
        _ = ProcessLoop(runtime, channel.Reader, cancellationToken);

        spawnActivity?.SetStatus(ActivityStatusCode.Ok);
        return address;
    }

    /// <summary>
    /// The actor's processing loop — reads commands from the mailbox one at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the heart of the actor: a single-threaded loop that processes one
    /// command at a time. This provides Hewitt's fundamental guarantee that an actor
    /// handles exactly one message before designating its next behavior.
    /// </para>
    /// <para>
    /// The loop runs until the channel is completed (no more writers) or the
    /// cancellation token is triggered. When the Decider reaches a terminal state
    /// (via <see cref="Decider{TState,TCommand,TEvent,TEffect,TError,TParameters}.IsTerminal"/>),
    /// the loop completes the channel writer (rejecting further commands) and exits.
    /// </para>
    /// </remarks>
    private static async Task ProcessLoop<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>(
        DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters> runtime,
        ChannelReader<TCommand> reader,
        CancellationToken cancellationToken)
        where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
    {
        try
        {
            await foreach (var command in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using var activity = AutomatonDiagnostics.Source.StartActivity("Actor.Handle");
                activity?.SetTag("actor.type", typeof(TDecider).Name);
                activity?.SetTag("actor.command.type", command?.GetType().Name);

                var result = await runtime.Handle(command, cancellationToken).ConfigureAwait(false);

                result.Switch(
                    _ =>
                    {
                        activity?.SetTag("actor.result", "ok");
                        activity?.SetStatus(ActivityStatusCode.Ok);
                    },
                    error =>
                    {
                        activity?.SetTag("actor.result", "error");
                        activity?.SetTag("actor.error.type", error?.GetType().Name);
                        activity?.SetStatus(ActivityStatusCode.Ok);
                    });

                // Terminal state: actor stops accepting new commands
                if (runtime.IsTerminal)
                {
                    activity?.SetTag("actor.terminal", true);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cooperative shutdown via CancellationToken — this is expected
        }
        finally
        {
            runtime.Dispose();
        }
    }
}
