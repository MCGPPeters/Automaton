// =============================================================================
// Automaton Kernel
// =============================================================================
// The fundamental abstraction underlying MVU, Event Sourcing, and the Actor Model.
//
// All three are instances of a Mealy machine — a finite-state transducer where
// outputs (effects) depend on both the current state and the input (event):
//
//     transition : (State × Event) → (State × Effect)
//
// Historical lineage:
//     Mealy Machine (1955) → Actor Model (Hewitt, 1973) → Erlang/OTP (1986)
//     → Event Sourcing (2005) → Elm Architecture (2012) → Automaton (2025)
//
// The same domain logic (transition function) can be executed by different
// runtimes: a browser UI loop (MVU), an event store (ES), or a mailbox (Actors).
// =============================================================================

namespace Automaton;

/// <summary>
/// A deterministic state machine with effects (Mealy machine).
/// </summary>
/// <remarks>
/// <para>
/// Given current state and an event, produces a new state and an effect
/// to be executed by the runtime. The transition function is pure —
/// all side effects are described as data and executed externally.
/// </para>
/// <para>
/// This is the shared kernel from which MVU, Event Sourcing, and the
/// Actor Model are derived as specialized runtimes.
/// </para>
/// <example>
/// <code>
/// public record CounterState(int Count);
///
/// public interface CounterEvent
/// {
///     record struct Increment : CounterEvent;
///     record struct Decrement : CounterEvent;
/// }
///
/// public interface CounterEffect
/// {
///     record struct None : CounterEffect;
/// }
///
/// public class Counter : Automaton&lt;CounterState, CounterEvent, CounterEffect&gt;
/// {
///     public static (CounterState, CounterEffect) Init() =&gt;
///         (new CounterState(0), new CounterEffect.None());
///
///     public static (CounterState, CounterEffect) Transition(CounterState state, CounterEvent @event) =&gt;
///         @event switch
///         {
///             CounterEvent.Increment =&gt; (state with { Count = state.Count + 1 }, new CounterEffect.None()),
///             CounterEvent.Decrement =&gt; (state with { Count = state.Count - 1 }, new CounterEffect.None()),
///             _ =&gt; throw new UnreachableException()
///         };
/// }
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TState">The state of the automaton.</typeparam>
/// <typeparam name="TEvent">The input events that drive transitions.</typeparam>
/// <typeparam name="TEffect">The output effects produced by transitions.</typeparam>
public interface Automaton<TState, TEvent, TEffect>
{
    /// <summary>
    /// Produces the initial state and any startup effects.
    /// </summary>
    static abstract (TState State, TEffect Effect) Init();

    /// <summary>
    /// Pure transition function: given state and event, produce new state and effect.
    /// </summary>
    static abstract (TState State, TEffect Effect) Transition(TState state, TEvent @event);
}
