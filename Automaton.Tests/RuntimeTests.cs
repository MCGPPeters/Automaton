// =============================================================================
// Shared Runtime Tests
// =============================================================================
// Proves the AutomatonRuntime correctly implements the monadic left fold
// with Observer and Interpreter extension points, using the Thermostat domain.
// =============================================================================

namespace Automaton.Tests;

public class RuntimeTests
{
    [Fact]
    public async Task Dispatch_UpdatesState()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOff());

        Assert.Equal(18m, runtime.State.CurrentTemp);
        Assert.False(runtime.State.Heating);
    }

    [Fact]
    public async Task Observer_ReceivesCorrectArguments()
    {
        var observed = new List<(ThermostatState State, ThermostatEvent Event, ThermostatEffect Effect)>();

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.Capture(observed), ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(25m));

        Assert.Single(observed);
        Assert.Equal(25m, observed[0].State.CurrentTemp);
        Assert.IsType<ThermostatEvent.TemperatureRecorded>(observed[0].Event);
        Assert.IsType<ThermostatEffect.None>(observed[0].Effect);
    }

    [Fact]
    public async Task Interpreter_FeedbackEventsAreDispatched()
    {
        var feedbackCount = 0;

        // Interpreter: on ActivateHeater effect, simulate a sensor reading (once)
        Interpreter<ThermostatEffect, ThermostatEvent> interpreter = effect =>
        {
            if (effect is ThermostatEffect.ActivateHeater && feedbackCount == 0)
            {
                feedbackCount++;
                return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                    Result<ThermostatEvent[], PipelineError>.Ok(
                    [new ThermostatEvent.TemperatureRecorded(19m)]));
            }

            return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                Result<ThermostatEvent[], PipelineError>.Ok([]));
        };

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, interpreter);

        // HeaterTurnedOn -> Heating=true, ActivateHeater effect -> interpreter returns TemperatureRecorded(19)
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());

        // State: CurrentTemp=19, Heating=true (HeaterTurnedOn + feedback TemperatureRecorded)
        Assert.Equal(19m, runtime.State.CurrentTemp);
        Assert.True(runtime.State.Heating);
    }

    [Fact]
    public async Task ObserverComposition_Then_BothObserversAreCalled()
    {
        var firstCalls = 0;
        var secondCalls = 0;

        Observer<ThermostatState, ThermostatEvent, ThermostatEffect> first = (_, _, _) =>
        {
            firstCalls++;
            return PipelineResult.Ok;
        };

        Observer<ThermostatState, ThermostatEvent, ThermostatEffect> second = (_, _, _) =>
        {
            secondCalls++;
            return PipelineResult.Ok;
        };

        var combined = first.Then(second);

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), combined, ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());

        Assert.Equal(2, firstCalls);
        Assert.Equal(2, secondCalls);
    }

    [Fact]
    public void Reset_ReplacesStateWithoutTransition()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        runtime.Reset(new ThermostatState(25m, 30m, true, true));

        Assert.Equal(25m, runtime.State.CurrentTemp);
        Assert.Equal(30m, runtime.State.TargetTemp);
        Assert.True(runtime.State.Heating);
        Assert.Empty(runtime.Events);
    }

    [Fact]
    public async Task Events_RecordedIncludingFeedback()
    {
        var feedbackCount = 0;

        Interpreter<ThermostatEffect, ThermostatEvent> interpreter = effect =>
        {
            if (effect is ThermostatEffect.ActivateHeater && feedbackCount == 0)
            {
                feedbackCount++;
                return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                    Result<ThermostatEvent[], PipelineError>.Ok(
                    [new ThermostatEvent.TemperatureRecorded(19m)]));
            }

            return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                Result<ThermostatEvent[], PipelineError>.Ok([]));
        };

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, interpreter);

        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());

        // Events: HeaterTurnedOn, TemperatureRecorded(19) (feedback)
        Assert.Equal(2, runtime.Events.Count);
        Assert.IsType<ThermostatEvent.HeaterTurnedOn>(runtime.Events[0]);
        Assert.IsType<ThermostatEvent.TemperatureRecorded>(runtime.Events[1]);
    }

    [Fact]
    public async Task Start_CreatesRuntimeAndInterpretsInitEffect()
    {
        var runtime = await AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .Start(default, ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        // Thermostat.Initialize() produces (CurrentTemp=20, TargetTemp=22, Heating=false, Active=true), None
        Assert.Equal(20m, runtime.State.CurrentTemp);
        Assert.Equal(22m, runtime.State.TargetTemp);
        Assert.False(runtime.State.Heating);
        Assert.True(runtime.State.Active);
        Assert.Empty(runtime.Events);
    }

    // =========================================================================
    // Thread Safety
    // =========================================================================

    [Fact]
    public async Task ConcurrentDispatches_AreSerializedAndProduceCorrectFinalState()
    {
        // Arrange: 100 concurrent temperature readings
        const int concurrency = 100;
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        // Act: fire all dispatches concurrently
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(15m)).AsTask())
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert: every event was applied — no lost updates
        Assert.Equal(15m, runtime.State.CurrentTemp);
        Assert.Equal(concurrency, runtime.Events.Count);
    }

    [Fact]
    public async Task ConcurrentMixedDispatches_ProduceCorrectFinalState()
    {
        // Arrange: 50 HeaterTurnedOn + 30 HeaterTurnedOff = 80 events total
        const int onCount = 50;
        const int offCount = 30;

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        // Act: interleave on/off events concurrently
        var onTasks = Enumerable.Range(0, onCount)
            .Select(_ => runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn()).AsTask());
        var offTasks = Enumerable.Range(0, offCount)
            .Select(_ => runtime.Dispatch(new ThermostatEvent.HeaterTurnedOff()).AsTask());

        await Task.WhenAll(onTasks.Concat(offTasks));

        // Assert: all events were serialized — no lost updates
        Assert.Equal(onCount + offCount, runtime.Events.Count);
    }

    // =========================================================================
    // Cancellation
    // =========================================================================

    [Fact]
    public async Task Dispatch_ThrowsWhenCancelled()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(25m), cts.Token).AsTask());

        // State should be unchanged
        Assert.Equal(20m, runtime.State.CurrentTemp);
    }

    [Fact]
    public async Task InterpretEffect_ThrowsWhenCancelled()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.InterpretEffect(new ThermostatEffect.None(), cts.Token).AsTask());
    }

    [Fact]
    public async Task Start_ThrowsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
                .Start(default, ThermostatObservers.NoOp, ThermostatInterpreters.NoOp, cancellationToken: cts.Token).AsTask());
    }

    [Fact]
    public async Task CancellationDuringFeedbackLoop_StopsProcessing()
    {
        using var cts = new CancellationTokenSource();
        var interpreterCalls = 0;

        // Interpreter: SendNotification -> [AlertRaised] -> SendNotification -> ... infinite loop.
        // Cancel after the 2nd interpreter call.
        Interpreter<ThermostatEffect, ThermostatEvent> cancellingInterpreter = effect =>
        {
            interpreterCalls++;
            if (interpreterCalls >= 2)
            {
                cts.Cancel();
            }
            if (effect is ThermostatEffect.SendNotification)
            {
                return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                    Result<ThermostatEvent[], PipelineError>.Ok(
                    [new ThermostatEvent.AlertRaised("loop")]));
            }
            return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                Result<ThermostatEvent[], PipelineError>.Ok([]));
        };

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, cancellingInterpreter);

        // AlertRaised -> SendNotification effect -> interpreter returns [AlertRaised] -> loop
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.Dispatch(new ThermostatEvent.AlertRaised("test"), cts.Token).AsTask());

        // The loop was stopped before depth 64
        Assert.True(interpreterCalls >= 2);
        Assert.True(interpreterCalls < AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>.MaxFeedbackDepth);
    }

    // =========================================================================
    // Feedback Depth Guard
    // =========================================================================

    [Fact]
    public async Task FeedbackLoop_ThrowsAtMaxDepth()
    {
        // Interpreter: SendNotification -> [AlertRaised] -> SendNotification -> ... infinite loop
        // AlertRaised produces SendNotification effect, creating a cycle
        Interpreter<ThermostatEffect, ThermostatEvent> runawayInterpreter = effect =>
        {
            if (effect is ThermostatEffect.SendNotification)
            {
                return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                    Result<ThermostatEvent[], PipelineError>.Ok(
                    [new ThermostatEvent.AlertRaised("loop")]));
            }
            return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                Result<ThermostatEvent[], PipelineError>.Ok([]));
        };

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, runawayInterpreter);

        // AlertRaised -> SendNotification -> AlertRaised -> ... -> depth exceeded
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Dispatch(new ThermostatEvent.AlertRaised("test")).AsTask());

        Assert.Contains("maximum depth", ex.Message);
        Assert.Contains(AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .MaxFeedbackDepth.ToString(), ex.Message);
    }

    [Fact]
    public void MaxFeedbackDepth_Is64()
    {
        Assert.Equal(64, AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>.MaxFeedbackDepth);
    }

    // =========================================================================
    // Null Safety
    // =========================================================================

    [Fact]
    public void Constructor_ThrowsOnNullObserver()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
                new ThermostatState(20m, 22m, false, true), null!, ThermostatInterpreters.NoOp));
    }

    [Fact]
    public void Constructor_ThrowsOnNullInterpreter()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
                new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, null!));
    }

    // =========================================================================
    // Unserialized (threadSafe=false)
    // =========================================================================

    [Fact]
    public async Task Dispatch_Unserialized_UpdatesState()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp,
            threadSafe: false);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));

        Assert.Equal(18m, runtime.State.CurrentTemp);
    }

    [Fact]
    public async Task Start_Unserialized_CreatesRuntime()
    {
        var runtime = await AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .Start(default, ThermostatObservers.NoOp, ThermostatInterpreters.NoOp, threadSafe: false);

        Assert.Equal(20m, runtime.State.CurrentTemp);
        Assert.True(runtime.State.Active);
    }

    [Fact]
    public async Task InterpretEffect_Unserialized_Works()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp,
            threadSafe: false);

        await runtime.InterpretEffect(new ThermostatEffect.None());

        Assert.Equal(20m, runtime.State.CurrentTemp); // state unchanged
    }

    // =========================================================================
    // Event Tracking Disabled (trackEvents=false)
    // =========================================================================

    [Fact]
    public async Task Dispatch_TrackingDisabled_DoesNotRecordEvents()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp,
            trackEvents: false);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());

        Assert.Equal(18m, runtime.State.CurrentTemp);
        Assert.True(runtime.State.Heating);
        Assert.Empty(runtime.Events); // no events recorded
    }

    [Fact]
    public async Task Dispatch_TrackingEnabled_RecordsEvents()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp,
            trackEvents: true);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));

        Assert.Single(runtime.Events);
    }

    // =========================================================================
    // Lean Mode (threadSafe=false, trackEvents=false)
    // =========================================================================

    [Fact]
    public async Task LeanMode_DispatchesCorrectly()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp,
            threadSafe: false, trackEvents: false);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());
        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(23m));
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOff());

        Assert.Equal(23m, runtime.State.CurrentTemp);
        Assert.False(runtime.State.Heating);
        Assert.Empty(runtime.Events);
    }

    [Fact]
    public async Task LeanMode_FeedbackLoopWorks()
    {
        var feedbackCount = 0;

        Interpreter<ThermostatEffect, ThermostatEvent> interpreter = effect =>
        {
            if (effect is ThermostatEffect.ActivateHeater && feedbackCount == 0)
            {
                feedbackCount++;
                return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                    Result<ThermostatEvent[], PipelineError>.Ok(
                    [new ThermostatEvent.TemperatureRecorded(19m)]));
            }
            return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                Result<ThermostatEvent[], PipelineError>.Ok([]));
        };

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, interpreter,
            threadSafe: false, trackEvents: false);

        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());

        Assert.Equal(19m, runtime.State.CurrentTemp);
        Assert.True(runtime.State.Heating);
        Assert.Empty(runtime.Events);
    }

    [Fact]
    public async Task LeanMode_Start_Works()
    {
        var runtime = await AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .Start(default, ThermostatObservers.NoOp, ThermostatInterpreters.NoOp,
                threadSafe: false, trackEvents: false);

        Assert.Equal(20m, runtime.State.CurrentTemp);
        Assert.Empty(runtime.Events);
    }

    // =========================================================================
    // Thread-Safe Reset
    // =========================================================================

    [Fact]
    public void Reset_ThreadSafe_AcquiresGate()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp,
            threadSafe: true);

        runtime.Reset(new ThermostatState(25m, 30m, true, true));

        Assert.Equal(25m, runtime.State.CurrentTemp);
        Assert.Equal(30m, runtime.State.TargetTemp);
    }

    [Fact]
    public void Reset_Unserialized_Works()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp,
            threadSafe: false);

        runtime.Reset(new ThermostatState(25m, 30m, true, true));

        Assert.Equal(25m, runtime.State.CurrentTemp);
    }

    [Fact]
    public async Task Reset_ThreadSafe_WaitsForInFlightDispatch()
    {
        // Verify Reset doesn't corrupt state when a dispatch is in-flight.
        // We use a slow observer to hold the gate, then verify Reset waits.
        var dispatchStarted = new TaskCompletionSource();
        var allowDispatchToFinish = new TaskCompletionSource();

        Observer<ThermostatState, ThermostatEvent, ThermostatEffect> slowObserver =
            async (_, _, _) =>
            {
                dispatchStarted.SetResult();
                await allowDispatchToFinish.Task;
                return Result<Unit, PipelineError>.Ok(Unit.Value);
            };

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), slowObserver, ThermostatInterpreters.NoOp,
            threadSafe: true);

        // Start a dispatch that will hold the gate via the slow observer
        var dispatchTask = runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m)).AsTask();
        await dispatchStarted.Task;

        // Reset should block because the gate is held — run it on a background thread
        var resetCompleted = false;
        var resetTask = Task.Run(() =>
        {
            runtime.Reset(new ThermostatState(99m, 99m, false, true));
            resetCompleted = true;
        });

        // Give Reset a moment — it should NOT complete yet
        await Task.Delay(50);
        Assert.False(resetCompleted, "Reset should be blocked while dispatch holds the gate");

        // Release the dispatch
        allowDispatchToFinish.SetResult();
        await dispatchTask;
        await resetTask;

        Assert.True(resetCompleted);
        Assert.Equal(99m, runtime.State.CurrentTemp);
    }

    // =========================================================================
    // IDisposable
    // =========================================================================

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        runtime.Dispose();
        runtime.Dispose(); // Should not throw
    }

    [Fact]
    public async Task Dispose_AfterUse_DoesNotThrow()
    {
        using var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
        Assert.Equal(18m, runtime.State.CurrentTemp);
    }
}
