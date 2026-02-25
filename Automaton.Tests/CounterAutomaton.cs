// =============================================================================
// Counter Automaton — Shared Domain Logic
// =============================================================================
// The SAME transition function used by all three runtimes (MVU, ES, Actor).
// This proves the automaton kernel is truly runtime-agnostic.
// =============================================================================

using System.Diagnostics;

namespace Automaton.Tests;

/// <summary>
/// The state of the counter.
/// </summary>
public readonly record struct CounterState(int Count);

/// <summary>
/// Events that can occur in the counter domain.
/// </summary>
public interface CounterEvent
{
    record struct Increment : CounterEvent;
    record struct Decrement : CounterEvent;
    record struct Reset : CounterEvent;
}

/// <summary>
/// Effects produced by counter transitions.
/// </summary>
public interface CounterEffect
{
    record struct None : CounterEffect;
    record struct Log(string Message) : CounterEffect;
}

/// <summary>
/// The counter automaton — pure domain logic, no runtime dependency.
/// </summary>
/// <remarks>
/// This single definition drives:
/// - A browser UI (MVU runtime)
/// - An event-sourced aggregate (ES runtime)
/// - A mailbox actor (Actor runtime)
/// </remarks>
public class Counter : Automaton<CounterState, CounterEvent, CounterEffect>
{
    /// <summary>
    /// Initial state: count is zero, no startup effects.
    /// </summary>
    public static (CounterState State, CounterEffect Effect) Init() =>
        (new CounterState(0), new CounterEffect.None());

    /// <summary>
    /// Pure transition: given state and event, produce new state and effect.
    /// </summary>
    public static (CounterState State, CounterEffect Effect) Transition(
        CounterState state,
        CounterEvent @event) =>
        @event switch
        {
            CounterEvent.Increment =>
                (state with { Count = state.Count + 1 }, new CounterEffect.None()),

            CounterEvent.Decrement =>
                (state with { Count = state.Count - 1 }, new CounterEffect.None()),

            CounterEvent.Reset =>
                (new CounterState(0), new CounterEffect.Log($"Counter reset from {state.Count}")),

            _ => throw new UnreachableException()
        };
}
