// =============================================================================
// Cross-Runtime Tests
// =============================================================================
// Proves the SAME automaton (Thermostat) produces identical results when
// executed by the MVU, Event Sourcing, and Actor runtimes.
//
// The transition function is the invariant. The runtime is the variable.
//
// MVU and Actor receive events directly (Automaton runtimes).
// ES receives commands (Decider runtime) — commands are validated by Decide,
// producing the same events that MVU/Actor receive directly.
// =============================================================================

using Automaton.Actor;
using Automaton.EventSourcing;
using Automaton.Mvu;

namespace Automaton.Tests;

public class CrossRuntimeTests
{
    /// <summary>
    /// The canonical event sequence used by MVU and Actor runtimes.
    /// Simulates: cold reading → heater on → warm reading → heater off.
    /// </summary>
    private static readonly ThermostatEvent[] _eventScenario =
    [
        new ThermostatEvent.TemperatureRecorded(18m),
        new ThermostatEvent.HeaterTurnedOn(),
        new ThermostatEvent.TemperatureRecorded(23m),
        new ThermostatEvent.HeaterTurnedOff()
    ];

    /// <summary>
    /// The equivalent command sequence used by the ES runtime.
    /// Commands express intent; Decide validates and produces the same events.
    /// </summary>
    /// <remarks>
    /// RecordReading(18) when target=22 and not heating → [TemperatureRecorded(18), HeaterTurnedOn]
    /// RecordReading(23) when target=22 and heating → [TemperatureRecorded(23), HeaterTurnedOff]
    /// </remarks>
    private static readonly ThermostatCommand[] _commandScenario =
    [
        new ThermostatCommand.RecordReading(18m),
        new ThermostatCommand.RecordReading(23m)
    ];

    /// <summary>
    /// Expected final state: CurrentTemp=23, TargetTemp=22, Heating=false, Active=true
    /// </summary>
    private static readonly ThermostatState _expectedFinalState =
        new(CurrentTemp: 23m, TargetTemp: 22m, Heating: false, Active: true);

    [Fact]
    public async Task AllThreeRuntimes_ProduceIdenticalFinalState()
    {
        // --- MVU (receives events) ---
        var mvu = await MvuRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, string>
            .Start(
                s => $"{s.CurrentTemp}°C (target: {s.TargetTemp}°C, heating: {s.Heating})",
                _ => new ValueTask<ThermostatEvent[]>([]));

        foreach (var e in _eventScenario)
        {
            await mvu.Dispatch(e);
        }

        // --- Event Sourcing (receives commands) ---
        var aggregate = AggregateRunner<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError>.Create();

        foreach (var cmd in _commandScenario)
        {
            aggregate.Handle(cmd);
        }

        // --- Actor (receives events) ---
        var actor = ActorInstance<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect>
            .Spawn("cross-test");

        foreach (var e in _eventScenario)
        {
            await actor.Ref.Tell(e);
        }

        await actor.DrainMailbox();

        // --- All three produce identical state ---
        Assert.Equal(_expectedFinalState, mvu.State);
        Assert.Equal(_expectedFinalState, aggregate.State);
        Assert.Equal(_expectedFinalState, actor.State);

        // --- And they all equal each other ---
        Assert.Equal(mvu.State, aggregate.State);
        Assert.Equal(aggregate.State, actor.State);

        await actor.Stop();
    }

    [Fact]
    public async Task EventSourcing_CanRebuild_ToSameStateAsMvu()
    {
        // Run through MVU (events)
        var mvu = await MvuRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, string>
            .Start(
                s => $"{s.CurrentTemp}°C",
                _ => new ValueTask<ThermostatEvent[]>([]));

        foreach (var e in _eventScenario)
        {
            await mvu.Dispatch(e);
        }

        // Run through ES (commands) and rebuild from scratch
        var aggregate = AggregateRunner<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError>.Create();

        foreach (var cmd in _commandScenario)
        {
            aggregate.Handle(cmd);
        }

        var rebuilt = aggregate.Rebuild();

        Assert.Equal(mvu.State, rebuilt);
    }

    [Fact]
    public void AutomatonTransition_IsPure_SameInputProducesSameOutput()
    {
        var state = new ThermostatState(20m, 22m, false, true);
        var @event = new ThermostatEvent.TemperatureRecorded(18m);

        var (state1, effect1) = Thermostat.Transition(state, @event);
        var (state2, effect2) = Thermostat.Transition(state, @event);

        Assert.Equal(state1, state2);
        Assert.Equal(effect1, effect2);
    }

    [Fact]
    public void AutomatonTransition_IsALeftFold()
    {
        // state = events.Aggregate(init, transition)
        // This IS event sourcing. This IS MVU. This IS actor state.
        var (seed, _) = Thermostat.Init();

        var finalState = _eventScenario.Aggregate(seed, (state, @event) =>
            Thermostat.Transition(state, @event).State);

        Assert.Equal(_expectedFinalState, finalState);
    }
}
