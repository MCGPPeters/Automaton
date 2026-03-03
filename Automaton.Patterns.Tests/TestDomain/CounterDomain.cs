// =============================================================================
// Counter Domain — Test Decider for Event Sourcing Tests
// =============================================================================
// A simplified Counter domain used to test AggregateRunner, InMemoryEventStore,
// Projection, and ResolvingAggregateRunner.
//
// Implements ConflictResolver because counter increments/decrements are
// commutative — two concurrent "Add 3" commands produce the same final state
// regardless of order, as long as the result stays within bounds [0, MaxCount].
// =============================================================================

using System.Diagnostics;
using Automaton.Patterns.EventSourcing;

namespace Automaton.Patterns.Tests.TestDomain;

public readonly record struct CounterState(int Count);

public interface CounterCommand
{
    record struct Add(int Amount) : CounterCommand;
    record struct Reset : CounterCommand;
}

public interface CounterEvent
{
    record struct Incremented : CounterEvent;
    record struct Decremented : CounterEvent;
    record struct WasReset : CounterEvent;
}

public interface CounterError
{
    record struct Overflow(int Current, int Amount, int Max) : CounterError;
    record struct Underflow(int Current, int Amount) : CounterError;
    record struct AlreadyAtZero : CounterError;
}

public interface CounterEffect
{
    record struct None : CounterEffect;
    record struct Log(string Message) : CounterEffect;
}

public class CounterDecider
    : ConflictResolver<CounterState, CounterCommand, CounterEvent, CounterEffect, CounterError, Unit>
{
    public const int MaxCount = 100;

    public static (CounterState State, CounterEffect Effect) Initialize(Unit _) =>
        (new CounterState(0), new CounterEffect.None());

    public static Result<CounterEvent[], CounterError> Decide(
        CounterState state,
        CounterCommand command) =>
        command switch
        {
            CounterCommand.Add(var amount) when state.Count + amount > MaxCount =>
                Result<CounterEvent[], CounterError>
                    .Err(new CounterError.Overflow(state.Count, amount, MaxCount)),

            CounterCommand.Add(var amount) when state.Count + amount < 0 =>
                Result<CounterEvent[], CounterError>
                    .Err(new CounterError.Underflow(state.Count, amount)),

            CounterCommand.Add(var amount) when amount >= 0 =>
                Result<CounterEvent[], CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(new CounterEvent.Incremented(), amount).ToArray()),

            CounterCommand.Add(var amount) =>
                Result<CounterEvent[], CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(new CounterEvent.Decremented(), Math.Abs(amount)).ToArray()),

            CounterCommand.Reset when state.Count is 0 =>
                Result<CounterEvent[], CounterError>
                    .Err(new CounterError.AlreadyAtZero()),

            CounterCommand.Reset =>
                Result<CounterEvent[], CounterError>
                    .Ok([new CounterEvent.WasReset()]),

            _ => throw new UnreachableException()
        };

    public static (CounterState State, CounterEffect Effect) Transition(
        CounterState state,
        CounterEvent @event) =>
        @event switch
        {
            CounterEvent.Incremented =>
                (state with { Count = state.Count + 1 }, new CounterEffect.None()),

            CounterEvent.Decremented =>
                (state with { Count = state.Count - 1 }, new CounterEffect.None()),

            CounterEvent.WasReset =>
                (new CounterState(0), new CounterEffect.Log($"Counter reset from {state.Count}")),

            _ => throw new UnreachableException()
        };

    /// <summary>
    /// Resolves concurrency conflicts for counter operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Increments and decrements are commutative — they can be safely replayed
    /// on the merged state as long as the result stays within [0, MaxCount].
    /// The runner pre-computes <paramref name="projectedState"/> via <c>Transition</c>,
    /// so we just check invariants on the result.
    /// </para>
    /// <para>
    /// Reset events conflict with any concurrent changes because reset is
    /// non-commutative (it depends on the exact count at the time of reset).
    /// </para>
    /// </remarks>
    public static Result<CounterEvent[], ConflictNotResolved> ResolveConflicts(
        CounterState currentState,
        CounterState projectedState,
        CounterEvent[] ourEvents,
        IReadOnlyList<CounterEvent> theirEvents)
    {
        // Reset is non-commutative — cannot be resolved
        if (ourEvents.Any(e => e is CounterEvent.WasReset))
            return Result<CounterEvent[], ConflictNotResolved>.Err(
                new ConflictNotResolved(
                    "Reset conflicts with concurrent changes — reset depends on exact count."));

        if (theirEvents.Any(e => e is CounterEvent.WasReset))
            return Result<CounterEvent[], ConflictNotResolved>.Err(
                new ConflictNotResolved(
                    "Cannot apply changes after a concurrent reset — state has been zeroed."));

        // Validate invariants on the projected state (pre-computed by the runner)
        return projectedState.Count switch
        {
            > MaxCount => Result<CounterEvent[], ConflictNotResolved>.Err(
                new ConflictNotResolved(
                    $"Resolved count {projectedState.Count} would exceed maximum {MaxCount}.")),
            < 0 => Result<CounterEvent[], ConflictNotResolved>.Err(
                new ConflictNotResolved(
                    $"Resolved count {projectedState.Count} would go below zero.")),
            _ => Result<CounterEvent[], ConflictNotResolved>.Ok(ourEvents)
        };
    }
}
