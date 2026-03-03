// =============================================================================
// Automaton Benchmarks — Hot path performance measurements
// =============================================================================
// Measures the core runtime overhead:
//   • Dispatch (single, batch, with observer, with interpreter feedback)
//   • DecidingRuntime.Handle (accept / reject)
//   • Observer composition via Then combinator
//
// Uses a deliberately trivial domain (BenchDomain) to isolate framework cost.
// =============================================================================

using BenchmarkDotNet.Attributes;

namespace Automaton.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class AutomatonBenchmarks
{
    // ── Runtimes rebuilt per iteration to prevent list growth bias ────

    private AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit> _runtimeNoOp = null!;
    private AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit> _runtimeObserver = null!;
    private AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit> _runtimeFeedback = null!;
    private AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit> _runtimeComposed = null!;
    private DecidingRuntime<BenchDecider, BenchState, BenchCommand, BenchEvent, BenchEffect, BenchError, Unit> _decider = null!;

    // ── Safe-no-track runtimes (threadSafe=true, trackEvents=false) ─

    private AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit> _safeNoTrackNoOp = null!;
    private AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit> _safeNoTrackFeedback = null!;
    private DecidingRuntime<BenchDecider, BenchState, BenchCommand, BenchEvent, BenchEffect, BenchError, Unit> _safeNoTrackDecider = null!;

    // ── Lean runtimes (threadSafe=false, trackEvents=false) ──────────

    private AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit> _leanNoOp = null!;
    private AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit> _leanFeedback = null!;
    private DecidingRuntime<BenchDecider, BenchState, BenchCommand, BenchEvent, BenchEffect, BenchError, Unit> _leanDecider = null!;

    // ── Pre-allocated events / commands ──────────────────────────────

    private static readonly BenchEvent.Increment _singleEvent = new(1);
    private static readonly BenchEvent.WithEffect _effectEvent = new(1);
    private static readonly BenchCommand.Add _acceptCommand = new(1);
    private static readonly BenchCommand.Reject _rejectCommand = new();

    [IterationSetup]
    public void Setup()
    {
        var (initState, _) = BenchAutomaton.Initialize(default);

        _runtimeNoOp = new AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit>(
            initState, BenchObservers.NoOp, BenchInterpreters.NoOp);

        _runtimeObserver = new AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit>(
            initState, BenchObservers.Touch, BenchInterpreters.NoOp);

        _runtimeFeedback = new AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit>(
            initState, BenchObservers.NoOp, BenchInterpreters.SingleFeedback);

        var composed = BenchObservers.NoOp.Then(BenchObservers.Touch);
        _runtimeComposed = new AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit>(
            initState, composed, BenchInterpreters.NoOp);

        _decider = DecidingRuntime<BenchDecider, BenchState, BenchCommand, BenchEvent, BenchEffect, BenchError, Unit>
            .Start(default, BenchObservers.NoOp, BenchInterpreters.NoOp)
            .GetAwaiter().GetResult();

        // Lean runtimes — no semaphore, no event tracking
        _leanNoOp = new AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit>(
            initState, BenchObservers.NoOp, BenchInterpreters.NoOp,
            threadSafe: false, trackEvents: false);

        _leanFeedback = new AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit>(
            initState, BenchObservers.NoOp, BenchInterpreters.SingleFeedback,
            threadSafe: false, trackEvents: false);

        _leanDecider = DecidingRuntime<BenchDecider, BenchState, BenchCommand, BenchEvent, BenchEffect, BenchError, Unit>
            .Start(default, BenchObservers.NoOp, BenchInterpreters.NoOp,
                threadSafe: false, trackEvents: false)
            .GetAwaiter().GetResult();

        // Safe-no-track runtimes — thread-safe, but no event tracking
        _safeNoTrackNoOp = new AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit>(
            initState, BenchObservers.NoOp, BenchInterpreters.NoOp,
            threadSafe: true, trackEvents: false);

        _safeNoTrackFeedback = new AutomatonRuntime<BenchAutomaton, BenchState, BenchEvent, BenchEffect, Unit>(
            initState, BenchObservers.NoOp, BenchInterpreters.SingleFeedback,
            threadSafe: true, trackEvents: false);

        _safeNoTrackDecider = DecidingRuntime<BenchDecider, BenchState, BenchCommand, BenchEvent, BenchEffect, BenchError, Unit>
            .Start(default, BenchObservers.NoOp, BenchInterpreters.NoOp,
                threadSafe: true, trackEvents: false)
            .GetAwaiter().GetResult();
    }

    // ── Dispatch benchmarks ──────────────────────────────────────────

    [Benchmark(Description = "Dispatch (no-op observer, no-op interpreter)")]
    public ValueTask<Result<Unit, PipelineError>> Dispatch_Single()
        => _runtimeNoOp.Dispatch(_singleEvent);

    [Benchmark(Description = "Dispatch (observer touches state/event/effect)")]
    public ValueTask<Result<Unit, PipelineError>> Dispatch_WithObserver()
        => _runtimeObserver.Dispatch(_singleEvent);

    [Benchmark(Description = "Dispatch × 100 (batch, no-op)")]
    public async Task Dispatch_Batch_100()
    {
        for (var i = 0; i < 100; i++)
            _ = await _runtimeNoOp.Dispatch(_singleEvent);
    }

    [Benchmark(Description = "Dispatch with interpreter feedback (1 level)")]
    public ValueTask<Result<Unit, PipelineError>> Dispatch_WithFeedback()
        => _runtimeFeedback.Dispatch(_effectEvent);

    [Benchmark(Description = "Dispatch with composed observer (Then)")]
    public ValueTask<Result<Unit, PipelineError>> Dispatch_ComposedObserver()
        => _runtimeComposed.Dispatch(_singleEvent);

    // ── Decider benchmarks ───────────────────────────────────────────

    [Benchmark(Description = "Handle — accept (1 event dispatched)")]
    public ValueTask<Result<BenchState, BenchError>> Handle_Accept()
        => _decider.Handle(_acceptCommand);

    [Benchmark(Description = "Handle — reject (0 events, error returned)")]
    public ValueTask<Result<BenchState, BenchError>> Handle_Reject()
        => _decider.Handle(_rejectCommand);

    // ── Safe-no-track benchmarks (threadSafe=true, trackEvents=false) ─

    [Benchmark(Description = "Safe Dispatch (no tracking)")]
    public ValueTask<Result<Unit, PipelineError>> Safe_NoTrack_Dispatch_Single()
        => _safeNoTrackNoOp.Dispatch(_singleEvent);

    [Benchmark(Description = "Safe Dispatch with feedback (no tracking)")]
    public ValueTask<Result<Unit, PipelineError>> Safe_NoTrack_Dispatch_WithFeedback()
        => _safeNoTrackFeedback.Dispatch(_effectEvent);

    [Benchmark(Description = "Safe Handle — accept (no tracking)")]
    public ValueTask<Result<BenchState, BenchError>> Safe_NoTrack_Handle_Accept()
        => _safeNoTrackDecider.Handle(_acceptCommand);

    [Benchmark(Description = "Safe Handle — reject (no tracking)")]
    public ValueTask<Result<BenchState, BenchError>> Safe_NoTrack_Handle_Reject()
        => _safeNoTrackDecider.Handle(_rejectCommand);

    // ── Lean benchmarks (threadSafe=false, trackEvents=false) ────────

    [Benchmark(Description = "Lean Dispatch (no-op, unserialized, no tracking)")]
    public ValueTask<Result<Unit, PipelineError>> Lean_Dispatch_Single()
        => _leanNoOp.Dispatch(_singleEvent);

    [Benchmark(Description = "Lean Dispatch with feedback (unserialized, no tracking)")]
    public ValueTask<Result<Unit, PipelineError>> Lean_Dispatch_WithFeedback()
        => _leanFeedback.Dispatch(_effectEvent);

    [Benchmark(Description = "Lean Handle — accept (unserialized, no tracking)")]
    public ValueTask<Result<BenchState, BenchError>> Lean_Handle_Accept()
        => _leanDecider.Handle(_acceptCommand);

    [Benchmark(Description = "Lean Handle — reject (unserialized, no tracking)")]
    public ValueTask<Result<BenchState, BenchError>> Lean_Handle_Reject()
        => _leanDecider.Handle(_rejectCommand);
}
