// =============================================================================
// MVU Runtime Tests
// =============================================================================
// Proves the thermostat automaton works as an MVU application:
// dispatch events → state transitions → views rendered → effects handled.
// =============================================================================

using System.Globalization;
using Automaton.Mvu;

namespace Automaton.Tests;

public class MvuRuntimeTests
{
    /// <summary>
    /// Simple view: renders thermostat state as a status string.
    /// Uses InvariantCulture so decimal formatting is locale-independent.
    /// </summary>
    private static string RenderThermostat(ThermostatState state) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{state.CurrentTemp}°C (target: {state.TargetTemp}°C, heating: {state.Heating})");

    [Fact]
    public async Task Init_ProducesInitialState_AndRendersInitialView()
    {
        var runtime = await MvuRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, string, Unit>
            .Start(RenderThermostat, ThermostatInterpreters.NoOp);

        Assert.Equal(20m, runtime.State.CurrentTemp);
        Assert.Single(runtime.Views);
        Assert.Equal("20.0°C (target: 22.0°C, heating: False)", runtime.Views[0]);
    }

    [Fact]
    public async Task Dispatch_TemperatureRecorded_UpdatesStateAndRendersNewView()
    {
        var runtime = await MvuRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, string, Unit>
            .Start(RenderThermostat, ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));

        Assert.Equal(18m, runtime.State.CurrentTemp);
        Assert.Equal(2, runtime.Views.Count);
        Assert.Equal("18°C (target: 22.0°C, heating: False)", runtime.Views[1]);
    }

    [Fact]
    public async Task Dispatch_MultipleEvents_ProducesCorrectSequenceOfViews()
    {
        var runtime = await MvuRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, string, Unit>
            .Start(RenderThermostat, ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());
        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(23m));
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOff());

        Assert.Equal(23m, runtime.State.CurrentTemp);
        Assert.False(runtime.State.Heating);
        Assert.Equal(5, runtime.Views.Count); // initialization + 4 dispatches
        Assert.Equal("20.0°C (target: 22.0°C, heating: False)", runtime.Views[0]);
        Assert.Equal("18°C (target: 22.0°C, heating: False)", runtime.Views[1]);
        Assert.Equal("18°C (target: 22.0°C, heating: True)", runtime.Views[2]);
        Assert.Equal("23°C (target: 22.0°C, heating: True)", runtime.Views[3]);
        Assert.Equal("23°C (target: 22.0°C, heating: False)", runtime.Views[4]);
    }

    [Fact]
    public async Task Dispatch_ShutdownCompleted_ProducesSendNotificationEffect()
    {
        var notifications = new List<string>();

        var runtime = await MvuRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, string, Unit>
            .Start(RenderThermostat, ThermostatInterpreters.CaptureNotifications(notifications));

        await runtime.Dispatch(new ThermostatEvent.ShutdownCompleted());

        Assert.False(runtime.State.Active);
        Assert.Single(notifications);
        Assert.Equal("Thermostat shut down", notifications[0]);
    }

    [Fact]
    public async Task Events_AreRecorded()
    {
        var runtime = await MvuRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, string, Unit>
            .Start(RenderThermostat, ThermostatInterpreters.NoOp);

        await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
        await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());

        Assert.Equal(2, runtime.Events.Count);
        Assert.IsType<ThermostatEvent.TemperatureRecorded>(runtime.Events[0]);
        Assert.IsType<ThermostatEvent.HeaterTurnedOn>(runtime.Events[1]);
    }
}
