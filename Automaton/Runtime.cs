// =============================================================================
// Automaton Runtime
// =============================================================================
// The shared runtime abstraction underlying MVU, Event Sourcing, and Actors.
//
// Mathematically, the runtime is a monadic left fold over an event stream:
//
//     foldM : (State -> Event -> M (State, Effect)) -> State -> [Event] -> M State
//
// It is parameterized by two extension points:
//
// 1. Observer  — sees each (State, Event, Effect) triple after transition.
//                Used for rendering (MVU), persisting (ES), or logging.
//
// 2. Interpreter — converts effects into feedback events.
//                   Used for effect handling / command execution.
//
// Every specialized runtime (MVU, ES, Actor) is an instance of this
// structure with specific Observer and Interpreter implementations.
// =============================================================================

namespace Automaton;

/// <summary>
/// Observes each transition triple (state, event, effect) after the automaton steps.
/// </summary>
/// <remarks>
/// The observer is the extension point for side effects that depend on the
/// transition result: rendering a view (MVU), persisting an event (ES),
/// or logging an audit trail.
/// </remarks>
/// <typeparam name="TState">The state produced by the transition.</typeparam>
/// <typeparam name="TEvent">The event that triggered the transition.</typeparam>
/// <typeparam name="TEffect">The effect produced by the transition.</typeparam>
public delegate Task Observer<in TState, in TEvent, in TEffect>(
    TState state,
    TEvent @event,
    TEffect effect);

/// <summary>
/// Interprets an effect by converting it into zero or more feedback events.
/// </summary>
/// <remarks>
/// The interpreter is the extension point for effect execution. Feedback
/// events are dispatched back into the automaton, creating a closed loop.
/// Return an empty sequence for fire-and-forget effects.
/// </remarks>
/// <typeparam name="TEffect">The effect to interpret.</typeparam>
/// <typeparam name="TEvent">The feedback events produced by interpretation.</typeparam>
public delegate Task<IEnumerable<TEvent>> Interpreter<in TEffect, TEvent>(TEffect effect);

/// <summary>
/// The shared automaton runtime: a monadic left fold with Observer and Interpreter.
/// </summary>
/// <remarks>
/// <para>
/// This is the structural core from which MVU, Event Sourcing, and the Actor Model
/// are derived. Each specialized runtime constructs an <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect}"/>
/// with domain-specific Observer and Interpreter implementations.
/// </para>
/// <example>
/// <code>
/// // Create a runtime with logging observer and no-op interpreter
/// Observer&lt;CounterState, CounterEvent, CounterEffect&gt; log =
///     (state, @event, effect) =&gt; { Console.WriteLine($"{@event} → {state}"); return Task.CompletedTask; };
///
/// Interpreter&lt;CounterEffect, CounterEvent&gt; noOp =
///     _ =&gt; Task.FromResult&lt;IEnumerable&lt;CounterEvent&gt;&gt;([]);
///
/// var runtime = await AutomatonRuntime&lt;Counter, CounterState, CounterEvent, CounterEffect&gt;
///     .Start(log, noOp);
///
/// await runtime.Dispatch(new CounterEvent.Increment());
/// // runtime.State.Count == 1
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TAutomaton">The automaton type providing Init and Transition.</typeparam>
/// <typeparam name="TState">The state of the automaton.</typeparam>
/// <typeparam name="TEvent">The events that drive transitions.</typeparam>
/// <typeparam name="TEffect">The effects produced by transitions.</typeparam>
public sealed class AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>
    where TAutomaton : Automaton<TState, TEvent, TEffect>
{
    private TState _state;
    private readonly Observer<TState, TEvent, TEffect> _observer;
    private readonly Interpreter<TEffect, TEvent> _interpreter;
    private readonly List<TEvent> _events = [];

    /// <summary>
    /// The current state of the automaton.
    /// </summary>
    public TState State => _state;

    /// <summary>
    /// All events dispatched during the lifetime of this runtime (including feedback events).
    /// </summary>
    public IReadOnlyList<TEvent> Events => _events;

    /// <summary>
    /// Creates a runtime with the given initial state, observer, and interpreter.
    /// </summary>
    /// <remarks>
    /// Use the constructor when you need to control initialization yourself
    /// (e.g., rendering an initial view before interpreting init effects).
    /// Use <see cref="Start"/> for the common case where init effects should
    /// be interpreted immediately.
    /// </remarks>
    public AutomatonRuntime(
        TState initialState,
        Observer<TState, TEvent, TEffect> observer,
        Interpreter<TEffect, TEvent> interpreter)
    {
        _state = initialState;
        _observer = observer;
        _interpreter = interpreter;
    }

    /// <summary>
    /// Creates and starts a runtime, interpreting init effects immediately.
    /// </summary>
    public static async Task<AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>> Start(
        Observer<TState, TEvent, TEffect> observer,
        Interpreter<TEffect, TEvent> interpreter)
    {
        var (state, effect) = TAutomaton.Init();
        var runtime = new AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>(state, observer, interpreter);
        await runtime.InterpretEffect(effect);
        return runtime;
    }

    /// <summary>
    /// Dispatches an event: transition → observe → interpret effects.
    /// </summary>
    public async Task Dispatch(TEvent @event)
    {
        _events.Add(@event);

        var (newState, effect) = TAutomaton.Transition(_state, @event);
        _state = newState;

        await _observer(_state, @event, effect);
        await InterpretEffect(effect);
    }

    /// <summary>
    /// Interprets an effect, dispatching any feedback events back into the loop.
    /// </summary>
    public async Task InterpretEffect(TEffect effect)
    {
        var feedbackEvents = await _interpreter(effect);
        foreach (var e in feedbackEvents)
        {
            await Dispatch(e);
        }
    }

    /// <summary>
    /// Replaces the current state without triggering a transition or observer.
    /// </summary>
    /// <remarks>
    /// Used by Event Sourcing to hydrate state from a replayed event stream,
    /// or by Actors for supervision/restart strategies.
    /// </remarks>
    public void Reset(TState state) => _state = state;
}

/// <summary>
/// Combinators for composing observers.
/// </summary>
public static class ObserverExtensions
{
    /// <summary>
    /// Composes two observers sequentially: <paramref name="first"/> runs, then <paramref name="second"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var combined = renderObserver.Then(logObserver);
    /// </code>
    /// </example>
    public static Observer<TState, TEvent, TEffect> Then<TState, TEvent, TEffect>(
        this Observer<TState, TEvent, TEffect> first,
        Observer<TState, TEvent, TEffect> second) =>
        async (state, @event, effect) =>
        {
            await first(state, @event, effect);
            await second(state, @event, effect);
        };
}
