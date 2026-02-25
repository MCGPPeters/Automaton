// =============================================================================
// Decider — Command Validation for Automatons
// =============================================================================
// The Decider pattern (Jérémie Chassaing, 2021) adds a command validation layer
// to the Automaton kernel. It separates:
//
//     intent  (Command)  →  decision  (Decide)  →  fact  (Event)  →  evolution  (Transition)
//
// Mathematically, Decide is a Kleisli arrow:
//
//     decide : Command → Reader<State, Result<Events, Error>>
//
// The Decider composes with the Automaton:
// - Automaton provides:  Transition (evolve) + Init (initial state)
// - Decider adds:        Decide (command validation) + IsTerminal (lifecycle)
//
// Together they form the seven elements of the Decider pattern:
//   1. Command type
//   2. Event type
//   3. State type
//   4. Initial state       (Init)
//   5. decide function      (Decide)
//   6. evolve function      (Transition)
//   7. isTerminal function  (IsTerminal)
//
// The same domain logic is reusable across runtimes — the Decide function is
// pure and testable independent of MVU, ES, or Actor infrastructure.
// =============================================================================

namespace Automaton;

/// <summary>
/// A Decider is an Automaton that validates commands before transitioning.
/// </summary>
/// <remarks>
/// <para>
/// The Decider pattern separates intent (commands) from facts (events).
/// Commands represent what the user wants to do; the <see cref="Decide"/> function
/// validates them against the current state and produces events (or rejects them).
/// Events then flow into <see cref="Automaton{TState,TEvent,TEffect}.Transition"/>
/// for pure state evolution.
/// </para>
/// <para>
/// This is the universal abstraction for command validation in event-driven systems.
/// It works identically across MVU, Event Sourcing, and Actor runtimes.
/// </para>
/// <example>
/// <code>
/// public class BoundedCounter
///     : Decider&lt;CounterState, CounterCommand, CounterEvent, CounterEffect, CounterError&gt;
/// {
///     public static (CounterState, CounterEffect) Init() =&gt;
///         (new CounterState(0), new CounterEffect.None());
///
///     public static Result&lt;IEnumerable&lt;CounterEvent&gt;, CounterError&gt; Decide(
///         CounterState state, CounterCommand command) =&gt;
///         command switch
///         {
///             CounterCommand.Add(var n) when state.Count + n &gt; 100 =&gt;
///                 new Result&lt;IEnumerable&lt;CounterEvent&gt;, CounterError&gt;
///                     .Err(new CounterError.Overflow(state.Count, n, 100)),
///             CounterCommand.Add(var n) =&gt;
///                 new Result&lt;IEnumerable&lt;CounterEvent&gt;, CounterError&gt;
///                     .Ok(Enumerable.Repeat&lt;CounterEvent&gt;(new CounterEvent.Increment(), n)),
///             _ =&gt; throw new UnreachableException()
///         };
///
///     public static (CounterState, CounterEffect) Transition(
///         CounterState state, CounterEvent @event) =&gt; ...;
/// }
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TState">The state of the automaton.</typeparam>
/// <typeparam name="TCommand">Commands representing user intent.</typeparam>
/// <typeparam name="TEvent">Events representing validated facts.</typeparam>
/// <typeparam name="TEffect">Effects produced by transitions.</typeparam>
/// <typeparam name="TError">Errors produced by invalid commands.</typeparam>
public interface Decider<TState, TCommand, TEvent, TEffect, TError>
    : Automaton<TState, TEvent, TEffect>
{
    /// <summary>
    /// Validates a command against the current state, producing events or an error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This function MUST be pure: its return value depends only on the inputs,
    /// and it produces no side effects. All external data (time, exchange rates, etc.)
    /// must be included in the command before calling Decide.
    /// </para>
    /// <para>
    /// When the command is valid, return <c>Ok</c> with the events that occurred.
    /// When invalid, return <c>Err</c> with a domain-specific error.
    /// An empty event list means "accepted but nothing happened" (idempotent command).
    /// </para>
    /// </remarks>
    static abstract Result<IEnumerable<TEvent>, TError> Decide(TState state, TCommand command);

    /// <summary>
    /// Whether the automaton has reached a terminal state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Terminal states signal that no further commands should be processed.
    /// Used by infrastructure for lifecycle management (archiving, disposal).
    /// </para>
    /// <para>
    /// Defaults to <c>false</c> (never terminal). Override when the domain
    /// has a natural end-of-life (e.g., a game that ends, an order that ships).
    /// </para>
    /// </remarks>
    static virtual bool IsTerminal(TState state) => false;
}

/// <summary>
/// Runtime that validates commands via <see cref="Decider{TState,TCommand,TEvent,TEffect,TError}.Decide"/>
/// before dispatching events through the <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The deciding runtime composes the Decider's validation layer with the
/// shared Automaton runtime's event loop (Observer + Interpreter):
/// </para>
/// <para>
/// <c>Command → Decide(state) → Result&lt;Events, Error&gt; → [Dispatch each] → (State', Effects)</c>
/// </para>
/// <para>
/// On success, all produced events are dispatched sequentially through the
/// underlying AutomatonRuntime (triggering Observer and Interpreter as normal).
/// On error, no events are dispatched and state remains unchanged.
/// </para>
/// <example>
/// <code>
/// var runtime = await DecidingRuntime&lt;BoundedCounter, CounterState, CounterCommand,
///     CounterEvent, CounterEffect, CounterError&gt;.Start(observer, interpreter);
///
/// var result = await runtime.Handle(new CounterCommand.Add(5));
/// // result is Ok(CounterState { Count = 5 })
///
/// var overflow = await runtime.Handle(new CounterCommand.Add(200));
/// // overflow is Err(CounterError.Overflow { ... })
/// // state remains at 5
/// </code>
/// </example>
/// </remarks>
public sealed class DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError>
    where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError>
{
    private readonly AutomatonRuntime<TDecider, TState, TEvent, TEffect> _core;

    /// <summary>
    /// The current state of the automaton.
    /// </summary>
    public TState State => _core.State;

    /// <summary>
    /// All events dispatched during the lifetime of this runtime (including feedback events).
    /// </summary>
    public IReadOnlyList<TEvent> Events => _core.Events;

    /// <summary>
    /// Whether the automaton has reached a terminal state.
    /// </summary>
    public bool IsTerminal => TDecider.IsTerminal(_core.State);

    private DecidingRuntime(AutomatonRuntime<TDecider, TState, TEvent, TEffect> core)
    {
        _core = core;
    }

    /// <summary>
    /// Creates and starts a deciding runtime, interpreting init effects immediately.
    /// </summary>
    public static async Task<DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError>> Start(
        Observer<TState, TEvent, TEffect> observer,
        Interpreter<TEffect, TEvent> interpreter)
    {
        var core = await AutomatonRuntime<TDecider, TState, TEvent, TEffect>
            .Start(observer, interpreter);

        return new DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError>(core);
    }

    /// <summary>
    /// Validates and handles a command: Decide → Dispatch events → return new state or error.
    /// </summary>
    /// <remarks>
    /// If <see cref="Decider{TState,TCommand,TEvent,TEffect,TError}.Decide"/> returns
    /// <c>Ok(events)</c>, each event is dispatched through the underlying runtime
    /// (triggering transitions, observer, and interpreter). The final state is returned.
    /// If it returns <c>Err(error)</c>, no events are dispatched and state is unchanged.
    /// </remarks>
    public async Task<Result<TState, TError>> Handle(TCommand command) =>
        await TDecider.Decide(_core.State, command).Match<Task<Result<TState, TError>>>(
            async events =>
            {
                foreach (var e in events)
                {
                    await _core.Dispatch(e);
                }

                return new Result<TState, TError>.Ok(_core.State);
            },
            error => Task.FromResult<Result<TState, TError>>(
                new Result<TState, TError>.Err(error)));

    /// <summary>
    /// Replaces the current state without triggering a transition.
    /// Used for hydration from an event store or snapshot.
    /// </summary>
    public void Reset(TState state) => _core.Reset(state);
}
