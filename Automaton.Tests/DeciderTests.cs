// =============================================================================
// Decider Tests
// =============================================================================
// Tests the Decider pattern: command validation, error rejection, state
// invariants, and Result<TSuccess, TError> algebraic operations.
// Uses the Thermostat domain for all command/error scenarios.
// =============================================================================

namespace Automaton.Tests;

public class DeciderTests
{

    // =========================================================================
    // DecidingRuntime — Command Handling
    // =========================================================================

    [Fact]
    public async Task Handle_RecordReading_TransitionsState()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new ThermostatCommand.RecordReading(18m));

        Assert.True(result.IsOk);
        Assert.Equal(18m, result.Value.CurrentTemp);
        // RecordReading(18) when target=22, not heating → [TemperatureRecorded(18), HeaterTurnedOn]
        Assert.True(runtime.State.Heating);
    }

    [Fact]
    public async Task Handle_SetTarget_UpdatesTargetTemperature()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new ThermostatCommand.SetTarget(25m));

        Assert.True(result.IsOk);
        Assert.Equal(25m, result.Value.TargetTemp);
    }

    [Fact]
    public async Task Handle_RecordReading_SameAsTarget_NoHeaterChange()
    {
        var runtime = await CreateRuntime();

        // Init: CurrentTemp=20, TargetTemp=22, Heating=false
        // RecordReading(22) → temp >= target, not heating → just TemperatureRecorded
        var eventsBefore = runtime.Events.Count;

        var result = await runtime.Handle(new ThermostatCommand.RecordReading(22m));

        Assert.True(result.IsOk);
        Assert.Equal(22m, result.Value.CurrentTemp);
        Assert.False(result.Value.Heating);
        // Only 1 event: TemperatureRecorded(22)
        Assert.Equal(eventsBefore + 1, runtime.Events.Count);
    }

    [Fact]
    public async Task Handle_SetTarget_AboveMax_ReturnsInvalidTargetError()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new ThermostatCommand.SetTarget(Thermostat.MaxTarget + 1));

        Assert.True(result.IsErr);
        var invalidTarget = Assert.IsType<ThermostatError.InvalidTarget>(result.Error);
        Assert.Equal(Thermostat.MaxTarget + 1, invalidTarget.Target);
        Assert.Equal(Thermostat.MinTarget, invalidTarget.Min);
        Assert.Equal(Thermostat.MaxTarget, invalidTarget.Max);
        // State unchanged
        Assert.Equal(22m, runtime.State.TargetTemp);
    }

    [Fact]
    public async Task Handle_SetTarget_BelowMin_ReturnsInvalidTargetError()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new ThermostatCommand.SetTarget(Thermostat.MinTarget - 1));

        Assert.True(result.IsErr);
        Assert.IsType<ThermostatError.InvalidTarget>(result.Error);
        Assert.Equal(22m, runtime.State.TargetTemp);
    }

    [Fact]
    public async Task Handle_BoundaryValues_AcceptsExactMax()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new ThermostatCommand.SetTarget(Thermostat.MaxTarget));

        Assert.True(result.IsOk);
        Assert.Equal(Thermostat.MaxTarget, result.Value.TargetTemp);
    }

    [Fact]
    public async Task Handle_BoundaryValues_AcceptsExactMin()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new ThermostatCommand.SetTarget(Thermostat.MinTarget));

        Assert.True(result.IsOk);
        Assert.Equal(Thermostat.MinTarget, result.Value.TargetTemp);
    }

    [Fact]
    public async Task Handle_BoundaryValues_RejectsAboveMax()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new ThermostatCommand.SetTarget(Thermostat.MaxTarget));
        var result = await runtime.Handle(new ThermostatCommand.SetTarget(Thermostat.MaxTarget + 0.1m));

        Assert.True(result.IsErr);
    }

    [Fact]
    public async Task Handle_Shutdown_SetsTerminalState()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new ThermostatCommand.Shutdown());

        Assert.True(result.IsOk);
        Assert.False(result.Value.Active);
        Assert.True(runtime.IsTerminal);
    }

    [Fact]
    public async Task Handle_ShutdownWhenAlreadyShutDown_ReturnsAlreadyShutdownError()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new ThermostatCommand.Shutdown());
        var result = await runtime.Handle(new ThermostatCommand.Shutdown());

        Assert.True(result.IsErr);
        Assert.IsType<ThermostatError.AlreadyShutdown>(result.Error);
    }

    [Fact]
    public async Task Handle_CommandAfterShutdown_ReturnsSystemInactiveError()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new ThermostatCommand.Shutdown());
        var result = await runtime.Handle(new ThermostatCommand.RecordReading(25m));

        Assert.True(result.IsErr);
        Assert.IsType<ThermostatError.SystemInactive>(result.Error);
    }

    [Fact]
    public async Task Handle_ErrorDoesNotMutateState()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new ThermostatCommand.RecordReading(18m));
        var stateBeforeError = runtime.State;
        var eventCountBeforeError = runtime.Events.Count;

        // Invalid command — target out of range
        await runtime.Handle(new ThermostatCommand.SetTarget(50m));

        Assert.Equal(stateBeforeError, runtime.State);
        Assert.Equal(eventCountBeforeError, runtime.Events.Count);
    }

    [Fact]
    public async Task Handle_ObserverSeesAllTransitions()
    {
        var observed = new List<(ThermostatState State, ThermostatEvent Event, ThermostatEffect Effect)>();

        var runtime = await DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError>.Start(ThermostatObservers.Capture(observed), ThermostatInterpreters.NoOp);

        // RecordReading(18) when target=22, not heating → [TemperatureRecorded(18), HeaterTurnedOn]
        await runtime.Handle(new ThermostatCommand.RecordReading(18m));

        Assert.Equal(2, observed.Count);
        Assert.Equal(18m, observed[0].State.CurrentTemp);
        Assert.False(observed[0].State.Heating); // After TemperatureRecorded, before HeaterTurnedOn
        Assert.True(observed[1].State.Heating);  // After HeaterTurnedOn
    }

    [Fact]
    public async Task Handle_ObserverNotCalledOnError()
    {
        var observerCallCount = 0;
        Observer<ThermostatState, ThermostatEvent, ThermostatEffect> observer = (_, _, _) =>
        {
            observerCallCount++;
            return PipelineResult.Ok;
        };

        var runtime = await DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError>.Start(observer, ThermostatInterpreters.NoOp);

        // Invalid target — should not trigger observer
        await runtime.Handle(new ThermostatCommand.SetTarget(50m));

        Assert.Equal(0, observerCallCount);
    }

    [Fact]
    public async Task IsTerminal_InitiallyFalse_TrueAfterShutdown()
    {
        var runtime = await CreateRuntime();

        Assert.False(runtime.IsTerminal);

        await runtime.Handle(new ThermostatCommand.RecordReading(18m));
        Assert.False(runtime.IsTerminal);

        await runtime.Handle(new ThermostatCommand.Shutdown());
        Assert.True(runtime.IsTerminal);
    }

    // =========================================================================
    // Decide — Pure Function Tests (no runtime needed)
    // =========================================================================

    [Fact]
    public void Decide_IsPure_SameInputProducesSameOutput()
    {
        var state = new ThermostatState(20m, 22m, false, true);
        var command = (ThermostatCommand)new ThermostatCommand.RecordReading(18m);

        var result1 = Thermostat.Decide(state, command);
        var result2 = Thermostat.Decide(state, command);

        Assert.True(result1.IsOk);
        Assert.True(result2.IsOk);
        Assert.Equal(result1.Value.ToList(), result2.Value.ToList());
    }

    [Fact]
    public void Decide_RecordReading_Cold_ReturnsTemperatureRecordedAndHeaterTurnedOn()
    {
        var state = new ThermostatState(20m, 22m, false, true);

        var result = Thermostat.Decide(state, new ThermostatCommand.RecordReading(18m));

        Assert.True(result.IsOk);
        var events = result.Value.ToList();
        Assert.Equal(2, events.Count);
        Assert.IsType<ThermostatEvent.TemperatureRecorded>(events[0]);
        Assert.IsType<ThermostatEvent.HeaterTurnedOn>(events[1]);
    }

    [Fact]
    public void Decide_RecordReading_Hot_ReturnsAlertRaised()
    {
        var state = new ThermostatState(20m, 22m, false, true);

        var result = Thermostat.Decide(state, new ThermostatCommand.RecordReading(36m));

        Assert.True(result.IsOk);
        var events = result.Value.ToList();
        Assert.Equal(2, events.Count);
        Assert.IsType<ThermostatEvent.TemperatureRecorded>(events[0]);
        Assert.IsType<ThermostatEvent.AlertRaised>(events[1]);
    }

    // =========================================================================
    // Result<TSuccess, TError> — Algebraic Operations
    // =========================================================================

    [Fact]
    public void Result_Ok_IsOk()
    {
        var result = Result<int, string>.Ok(42);

        Assert.True(result.IsOk);
        Assert.False(result.IsErr);
    }

    [Fact]
    public void Result_Err_IsErr()
    {
        var result = Result<int, string>.Err("oops");

        Assert.False(result.IsOk);
        Assert.True(result.IsErr);
    }

    [Fact]
    public void Result_Match_DispatchesCorrectly()
    {
        var ok = Result<int, string>.Ok(42);
        var err = Result<int, string>.Err("fail");

        Assert.Equal("42", ok.Match(v => v.ToString(), e => e));
        Assert.Equal("fail", err.Match(v => v.ToString(), e => e));
    }

    [Fact]
    public void Result_Map_TransformsSuccess()
    {
        var ok = Result<int, string>.Ok(21);

        var mapped = ok.Map(v => v * 2);

        Assert.True(mapped.IsOk);
        Assert.Equal(42, mapped.Value);
    }

    [Fact]
    public void Result_Map_PreservesError()
    {
        var err = Result<int, string>.Err("fail");

        var mapped = err.Map(v => v * 2);

        Assert.True(mapped.IsErr);
        Assert.Equal("fail", mapped.Error);
    }

    [Fact]
    public void Result_Bind_ChainsSuccess()
    {
        var ok = Result<int, string>.Ok(21);

        var bound = ok.Bind(v => Result<string, string>.Ok($"value: {v * 2}"));

        Assert.True(bound.IsOk);
        Assert.Equal("value: 42", bound.Value);
    }

    [Fact]
    public void Result_Bind_ShortCircuitsOnError()
    {
        var err = Result<int, string>.Err("fail");

        var bound = err.Bind(v => Result<string, string>.Ok($"value: {v}"));

        Assert.True(bound.IsErr);
        Assert.Equal("fail", bound.Error);
    }

    [Fact]
    public void Result_MapError_TransformsError()
    {
        var err = Result<int, string>.Err("fail");

        var mapped = err.MapError(e => e.Length);

        Assert.True(mapped.IsErr);
        Assert.Equal(4, mapped.Error);
    }

    [Fact]
    public void Result_MapError_PreservesSuccess()
    {
        var ok = Result<int, string>.Ok(42);

        var mapped = ok.MapError(e => e.Length);

        Assert.True(mapped.IsOk);
        Assert.Equal(42, mapped.Value);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static async Task<DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
        ThermostatEvent, ThermostatEffect, ThermostatError>> CreateRuntime()
    {
        return await DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError>.Start(ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);
    }

    // =========================================================================
    // Concurrent Handle
    // =========================================================================

    [Fact]
    public async Task ConcurrentHandles_AreSerializedAndProduceCorrectFinalState()
    {
        var runtime = await CreateRuntime();

        // Fire 50 concurrent RecordReading commands
        var tasks = Enumerable.Range(0, 50)
            .Select(i => runtime.Handle(new ThermostatCommand.RecordReading(15m)).AsTask())
            .ToArray();

        await Task.WhenAll(tasks);

        // All should succeed (no state corruption)
        Assert.All(tasks, t => Assert.True(t.Result.IsOk));
        Assert.Equal(15m, runtime.State.CurrentTemp);
    }

    [Fact]
    public async Task ConcurrentHandles_MixedValidAndInvalid_StateConsistent()
    {
        var runtime = await CreateRuntime();

        // Mix valid and invalid commands concurrently
        var tasks = new List<Task<Result<ThermostatState, ThermostatError>>>();
        for (var i = 0; i < 20; i++)
        {
            // Even: valid command, Odd: invalid command
            tasks.Add(i % 2 == 0
                ? runtime.Handle(new ThermostatCommand.RecordReading(18m)).AsTask()
                : runtime.Handle(new ThermostatCommand.SetTarget(50m)).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // All valid commands should succeed, all invalid should fail
        for (var i = 0; i < 20; i++)
        {
            if (i % 2 == 0)
                Assert.True(results[i].IsOk);
            else
                Assert.True(results[i].IsErr);
        }

        // State should reflect the valid commands
        Assert.Equal(18m, runtime.State.CurrentTemp);
        Assert.Equal(22m, runtime.State.TargetTemp); // Unchanged — invalid commands rejected
    }

    // =========================================================================
    // Unserialized Handle (threadSafe=false)
    // =========================================================================

    [Fact]
    public async Task Handle_Unserialized_AcceptsValidCommand()
    {
        var runtime = await DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError>
            .Start(ThermostatObservers.NoOp, ThermostatInterpreters.NoOp, threadSafe: false);

        var result = await runtime.Handle(new ThermostatCommand.RecordReading(18m));

        Assert.True(result.IsOk);
        Assert.Equal(18m, result.Value.CurrentTemp);
    }

    [Fact]
    public async Task Handle_Unserialized_RejectsInvalidCommand()
    {
        var runtime = await DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError>
            .Start(ThermostatObservers.NoOp, ThermostatInterpreters.NoOp, threadSafe: false);

        var result = await runtime.Handle(new ThermostatCommand.SetTarget(50m));

        Assert.True(result.IsErr);
        Assert.Equal(22m, runtime.State.TargetTemp);
    }

    // =========================================================================
    // Lean Mode (threadSafe=false, trackEvents=false)
    // =========================================================================

    [Fact]
    public async Task Handle_LeanMode_WorksCorrectly()
    {
        var runtime = await DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError>
            .Start(ThermostatObservers.NoOp, ThermostatInterpreters.NoOp,
                threadSafe: false, trackEvents: false);

        var result = await runtime.Handle(new ThermostatCommand.RecordReading(18m));

        Assert.True(result.IsOk);
        Assert.Equal(18m, result.Value.CurrentTemp);
        Assert.True(runtime.State.Heating);
        Assert.Empty(runtime.Events); // No events tracked
    }

    // =========================================================================
    // IDisposable
    // =========================================================================

    [Fact]
    public async Task DecidingRuntime_Dispose_Works()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new ThermostatCommand.RecordReading(18m));

        runtime.Dispose();
        runtime.Dispose(); // Should not throw on double dispose
    }

    // =========================================================================
    // Result Edge Cases
    // =========================================================================

    [Fact]
    public void Result_Value_ThrowsOnErr()
    {
        var result = Result<int, string>.Err("fail");

        var ex = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("Err", ex.Message);
    }

    [Fact]
    public void Result_Error_ThrowsOnOk()
    {
        var result = Result<int, string>.Ok(42);

        var ex = Assert.Throws<InvalidOperationException>(() => result.Error);
        Assert.Contains("Ok", ex.Message);
    }

    [Fact]
    public void Result_ToString_Ok()
    {
        var result = Result<int, string>.Ok(42);

        Assert.Equal("Ok(42)", result.ToString());
    }

    [Fact]
    public void Result_ToString_Err()
    {
        var result = Result<int, string>.Err("fail");

        Assert.Equal("Err(fail)", result.ToString());
    }

    [Fact]
    public async Task Result_AsyncMatch_DispatchesCorrectly()
    {
        var ok = Result<int, string>.Ok(42);
        var err = Result<int, string>.Err("fail");

        var okResult = await ok.Match(
            v => Task.FromResult(v.ToString()),
            e => Task.FromResult(e));
        Assert.Equal("42", okResult);

        var errResult = await err.Match(
            v => Task.FromResult(v.ToString()),
            e => Task.FromResult(e));
        Assert.Equal("fail", errResult);
    }
}
