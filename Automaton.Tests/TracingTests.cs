// =============================================================================
// Tracing Tests
// =============================================================================
// Verifies that the Automaton runtime emits OpenTelemetry-compatible
// Activity spans via System.Diagnostics.ActivitySource.
// Uses the Thermostat domain throughout.
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Automaton.Tests;

public class TracingTests
{
    /// <summary>
    /// Collects activities emitted by the Automaton ActivitySource during a test.
    /// Uses <see cref="ConcurrentBag{T}"/> because <see cref="ActivityListener.ActivityStopped"/>
    /// may fire from any thread when xUnit runs tests in parallel.
    /// </summary>
    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;
        public ConcurrentBag<Activity> Activities { get; } = [];

        public ActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == AutomatonDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Activities.Add(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    // =========================================================================
    // Runtime Tracing
    // =========================================================================

    [Fact]
    public async Task Dispatch_EmitsTracingSpan()
    {
        using var collector = new ActivityCollector();

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));

        var dispatch = collector.Activities.FirstOrDefault(a => a.DisplayName == "Automaton.Dispatch");
        Assert.NotNull(dispatch);
        Assert.Equal("Thermostat", dispatch.GetTagItem("automaton.type"));
        Assert.Equal("TemperatureRecorded", dispatch.GetTagItem("automaton.event.type"));
        Assert.Equal(ActivityStatusCode.Ok, dispatch.Status);
    }

    [Fact]
    public async Task Start_EmitsTracingSpan()
    {
        using var collector = new ActivityCollector();

        _ = await AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .Start(default, ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        var start = collector.Activities.FirstOrDefault(a => a.DisplayName == "Automaton.Start");
        Assert.NotNull(start);
        Assert.Equal("Thermostat", start.GetTagItem("automaton.type"));
        Assert.Equal("ThermostatState", start.GetTagItem("automaton.state.type"));
        Assert.Equal(ActivityStatusCode.Ok, start.Status);
    }

    [Fact]
    public async Task InterpretEffect_EmitsTracingSpan()
    {
        using var collector = new ActivityCollector();

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        await runtime.InterpretEffect(new ThermostatEffect.None());

        var interpret = collector.Activities.FirstOrDefault(a => a.DisplayName == "Automaton.InterpretEffect");
        Assert.NotNull(interpret);
        Assert.Equal("Thermostat", interpret.GetTagItem("automaton.type"));
        Assert.Equal("None", interpret.GetTagItem("automaton.effect.type"));
        Assert.Equal(ActivityStatusCode.Ok, interpret.Status);
    }

    [Fact]
    public async Task Dispatch_SetsErrorStatusOnFailure()
    {
        using var collector = new ActivityCollector();

        Interpreter<ThermostatEffect, ThermostatEvent> throwingInterpreter =
            _ => throw new InvalidOperationException("test fault");

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, throwingInterpreter);

        // HeaterTurnedOn produces ActivateHeater effect -> interpreter throws
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn()).AsTask());

        var dispatch = collector.Activities.FirstOrDefault(a =>
            a.DisplayName == "Automaton.Dispatch"
            && a.Status == ActivityStatusCode.Error);
        Assert.NotNull(dispatch);
        Assert.Contains("test fault", dispatch.StatusDescription);
    }

    [Fact]
    public async Task MultipleDispatches_EmitMultipleSpans()
    {
        using var collector = new ActivityCollector();

        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());
        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(23m));

        // Filter by specific event types to avoid cross-test interference
        // (ActivityListener is process-global; parallel tests may add spans).
        var dispatches = collector.Activities
            .Where(a => a.DisplayName == "Automaton.Dispatch")
            .ToList();
        Assert.True(dispatches.Count >= 3,
            $"Expected at least 3 Dispatch spans, got {dispatches.Count}");

        // Verify we got the specific event types we dispatched
        var eventTypes = dispatches.Select(a => a.GetTagItem("automaton.event.type")).ToList();
        Assert.Contains("TemperatureRecorded", eventTypes);
        Assert.Contains("HeaterTurnedOn", eventTypes);
    }

    // =========================================================================
    // Decider Tracing
    // =========================================================================

    [Fact]
    public async Task DeciderHandle_EmitsTracingSpan_OnSuccess()
    {
        using var collector = new ActivityCollector();

        var runtime = await DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Start(default, ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        await runtime.Handle(new ThermostatCommand.RecordReading(18m));

        var handle = collector.Activities.FirstOrDefault(a => a.DisplayName == "Automaton.Decider.Handle");
        Assert.NotNull(handle);
        Assert.Equal("Thermostat", handle.GetTagItem("automaton.type"));
        Assert.Equal("RecordReading", handle.GetTagItem("automaton.command.type"));
        Assert.Equal("ok", handle.GetTagItem("automaton.result"));
        Assert.Equal(ActivityStatusCode.Ok, handle.Status);
    }

    [Fact]
    public async Task DeciderHandle_EmitsTracingSpan_OnRejection()
    {
        using var collector = new ActivityCollector();

        var runtime = await DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Start(default, ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        await runtime.Handle(new ThermostatCommand.SetTarget(50m)); // exceeds MaxTarget

        var handle = collector.Activities.FirstOrDefault(a =>
            a.DisplayName == "Automaton.Decider.Handle"
            && Equals(a.GetTagItem("automaton.command.type"), "SetTarget"));
        Assert.NotNull(handle);
        Assert.Equal("error", handle.GetTagItem("automaton.result"));
        Assert.Equal("InvalidTarget", handle.GetTagItem("automaton.error.type"));
        // Command rejection is NOT a fault — status should be Ok
        Assert.Equal(ActivityStatusCode.Ok, handle.Status);
    }

    [Fact]
    public async Task DeciderStart_EmitsTracingSpan()
    {
        using var collector = new ActivityCollector();

        _ = await DecidingRuntime<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Start(default, ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        var start = collector.Activities.FirstOrDefault(a => a.DisplayName == "Automaton.Decider.Start");
        Assert.NotNull(start);
        Assert.Equal("Thermostat", start.GetTagItem("automaton.type"));
        Assert.Equal(ActivityStatusCode.Ok, start.Status);
    }

    // =========================================================================
    // No-listener fast path
    // =========================================================================

    [Fact]
    public async Task Dispatch_WorksWithNoListener()
    {
        // No ActivityCollector — no listener registered.
        // Verify that the runtime still works correctly (StartActivity returns null).
        var runtime = new AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>(
            new ThermostatState(20m, 22m, false, true), ThermostatObservers.NoOp, ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(25m));

        Assert.Equal(25m, runtime.State.CurrentTemp);
    }
}
