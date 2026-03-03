// =============================================================================
// Benchmark Domain — Minimal domain types for measuring framework overhead
// =============================================================================
// Deliberately trivial so benchmarks measure the Automaton runtime,
// not the domain logic.
// =============================================================================

using System.Diagnostics;

namespace Automaton.Benchmarks;

// ── State ─────────────────────────────────────────────────────

public record BenchState(int Value);

// ── Events ────────────────────────────────────────────────────

public interface BenchEvent
{
    record struct Increment(int Amount) : BenchEvent;
    record struct WithEffect(int Amount) : BenchEvent;
}

// ── Effects ───────────────────────────────────────────────────

public interface BenchEffect
{
    record struct None : BenchEffect;
    record struct Trigger(int FeedbackAmount) : BenchEffect;
}

// ── Commands ──────────────────────────────────────────────────

public interface BenchCommand
{
    record struct Add(int Amount) : BenchCommand;
    record struct Reject : BenchCommand;
}

// ── Errors ────────────────────────────────────────────────────

public interface BenchError
{
    record struct Rejected : BenchError;
}

// ── Automaton (pure transitions) ──────────────────────────────

public class BenchAutomaton : Automaton<BenchState, BenchEvent, BenchEffect, Unit>
{
    public static (BenchState State, BenchEffect Effect) Init(Unit _) =>
        (new BenchState(0), new BenchEffect.None());

    public static (BenchState State, BenchEffect Effect) Transition(
        BenchState state, BenchEvent @event) =>
        @event switch
        {
            BenchEvent.Increment(var n) =>
                (new BenchState(state.Value + n), new BenchEffect.None()),

            BenchEvent.WithEffect(var n) =>
                (new BenchState(state.Value + n), new BenchEffect.Trigger(1)),

            _ => throw new UnreachableException()
        };
}

// ── Decider (adds command validation) ─────────────────────────

public class BenchDecider
    : Decider<BenchState, BenchCommand, BenchEvent, BenchEffect, BenchError, Unit>
{
    public static (BenchState State, BenchEffect Effect) Init(Unit _) =>
        BenchAutomaton.Init(default);

    public static (BenchState State, BenchEffect Effect) Transition(
        BenchState state, BenchEvent @event) =>
        BenchAutomaton.Transition(state, @event);

    public static Result<BenchEvent[], BenchError> Decide(
        BenchState state, BenchCommand command) =>
        command switch
        {
            BenchCommand.Add(var n) =>
                Result<BenchEvent[], BenchError>
                    .Ok([new BenchEvent.Increment(n)]),

            BenchCommand.Reject =>
                Result<BenchEvent[], BenchError>
                    .Err(new BenchError.Rejected()),

            _ => throw new UnreachableException()
        };
}

// ── Observers & Interpreters ──────────────────────────────────

public static class BenchObservers
{
    public static readonly Observer<BenchState, BenchEvent, BenchEffect> NoOp =
        (_, _, _) => PipelineResult.Ok;

    public static readonly Observer<BenchState, BenchEvent, BenchEffect> Touch =
        (state, @event, effect) =>
        {
            _ = state.Value;
            _ = @event;
            _ = effect;
            return PipelineResult.Ok;
        };
}

public static class BenchInterpreters
{
    public static readonly Interpreter<BenchEffect, BenchEvent> NoOp =
        _ => new ValueTask<Result<BenchEvent[], PipelineError>>(
            Result<BenchEvent[], PipelineError>.Ok([]));

    /// <summary>
    /// Interpreter that produces one feedback event per <see cref="BenchEffect.Trigger"/>.
    /// Used to benchmark the interpreter feedback loop (1 level deep).
    /// </summary>
    public static readonly Interpreter<BenchEffect, BenchEvent> SingleFeedback =
        effect => effect switch
        {
            BenchEffect.Trigger(var n) =>
                new ValueTask<Result<BenchEvent[], PipelineError>>(
                    Result<BenchEvent[], PipelineError>.Ok([new BenchEvent.Increment(n)])),
            _ =>
                new ValueTask<Result<BenchEvent[], PipelineError>>(
                    Result<BenchEvent[], PipelineError>.Ok([]))
        };
}
