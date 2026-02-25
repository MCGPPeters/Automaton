// =============================================================================
// Counter Automaton — Shared Domain Logic
// =============================================================================
// The SAME transition function used by all three runtimes (MVU, ES, Actor).
// This proves the automaton kernel is truly runtime-agnostic.
//
// The Counter also demonstrates the Decider pattern: commands (intent) are
// validated against state before producing events (facts).
// =============================================================================

using System.Diagnostics;

namespace Automaton.Tests;

/// <summary>
/// The state of the counter.
/// </summary>
public readonly record struct CounterState(int Count);

/// <summary>
/// Commands representing user intent for the counter.
/// </summary>
/// <remarks>
/// Commands are validated by <see cref="Counter.Decide"/> before
/// producing events. Invalid commands are rejected with errors.
/// </remarks>
public interface CounterCommand
{
    /// <summary>Add an amount to the counter (can be negative for subtraction).</summary>
    record struct Add(int Amount) : CounterCommand;

    /// <summary>Reset the counter to zero.</summary>
    record struct Reset : CounterCommand;
}

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
/// Errors produced when command validation fails.
/// </summary>
public interface CounterError
{
    /// <summary>The resulting count would exceed the upper bound.</summary>
    record struct Overflow(int Current, int Amount, int Max) : CounterError;

    /// <summary>The resulting count would go below zero.</summary>
    record struct Underflow(int Current, int Amount) : CounterError;

    /// <summary>Reset requested when counter is already at zero.</summary>
    record struct AlreadyAtZero : CounterError;
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
/// <para>
/// This single definition drives:
/// - A browser UI (MVU runtime)
/// - An event-sourced aggregate (ES runtime)
/// - A mailbox actor (Actor runtime)
/// - A command-validated runtime (Decider)
/// </para>
/// <para>
/// As a Decider, the Counter validates commands before producing events:
/// - <c>Add(amount)</c> validates bounds [0, <see cref="MaxCount"/>]
/// - <c>Reset</c> rejects if already at zero
/// </para>
/// </remarks>
public class Counter
    : Decider<CounterState, CounterCommand, CounterEvent, CounterEffect, CounterError>
{
    /// <summary>
    /// Upper bound for the counter value.
    /// </summary>
    public const int MaxCount = 100;

    /// <summary>
    /// Initial state: count is zero, no startup effects.
    /// </summary>
    public static (CounterState State, CounterEffect Effect) Init() =>
        (new CounterState(0), new CounterEffect.None());

    /// <summary>
    /// Validates a command against the current state, producing events or an error.
    /// </summary>
    /// <remarks>
    /// This is a pure function: given the same state and command, it always
    /// returns the same result. Validation rules:
    /// <list type="bullet">
    ///   <item>Count must remain in [0, <see cref="MaxCount"/>]</item>
    ///   <item>Reset is rejected when count is already zero</item>
    /// </list>
    /// </remarks>
    public static Result<IEnumerable<CounterEvent>, CounterError> Decide(
        CounterState state,
        CounterCommand command) =>
        command switch
        {
            CounterCommand.Add(var amount) when state.Count + amount > MaxCount =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Err(new CounterError.Overflow(state.Count, amount, MaxCount)),

            CounterCommand.Add(var amount) when state.Count + amount < 0 =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Err(new CounterError.Underflow(state.Count, amount)),

            CounterCommand.Add(var amount) when amount >= 0 =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(new CounterEvent.Increment(), amount)),

            CounterCommand.Add(var amount) =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(new CounterEvent.Decrement(), Math.Abs(amount))),

            CounterCommand.Reset when state.Count is 0 =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Err(new CounterError.AlreadyAtZero()),

            CounterCommand.Reset =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Ok(new CounterEvent[] { new CounterEvent.Reset() }),

            _ => throw new UnreachableException()
        };

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
