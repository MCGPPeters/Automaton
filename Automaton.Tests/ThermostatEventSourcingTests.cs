// =============================================================================
// Thermostat Event Sourcing Tests
// =============================================================================
// Proves the thermostat Decider works as an event-sourced aggregate:
// commands validated → events persisted → state rebuilt → projections built.
//
// Demonstrates:
// - Multi-event commands (RecordReading → TemperatureRecorded + HeaterTurnedOn)
// - Command rejection (invalid target, inactive system, already shutdown)
// - State rebuild from event stream
// - Hydration from existing store
// - Projections (temperature history, heater duty cycle, alert log)
// - Terminal state (Shutdown → IsTerminal)
// =============================================================================

using Automaton.EventSourcing;

namespace Automaton.Tests;

public class ThermostatEventSourcingTests
{
    // =========================================================================
    // Aggregate — Initialization
    // =========================================================================

    [Fact]
    public void Aggregate_InitialState_IsActive()
    {
        var aggregate = CreateAggregate();

        Assert.Equal(20.0m, aggregate.State.CurrentTemp);
        Assert.Equal(22.0m, aggregate.State.TargetTemp);
        Assert.False(aggregate.State.Heating);
        Assert.True(aggregate.State.Active);
        Assert.False(aggregate.IsTerminal);
    }

    // =========================================================================
    // Aggregate — RecordReading
    // =========================================================================

    [Fact]
    public void RecordReading_BelowTarget_TurnsHeaterOn()
    {
        var aggregate = CreateAggregate();

        var result = aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));

        Assert.True(result.IsOk);
        Assert.Equal(18.0m, result.Value.CurrentTemp);
        Assert.True(result.Value.Heating);

        // Two events: TemperatureRecorded + HeaterTurnedOn
        Assert.Equal(2, aggregate.Store.Events.Count);
        Assert.IsType<ThermostatEvent.TemperatureRecorded>(aggregate.Store.Events[0].Event);
        Assert.IsType<ThermostatEvent.HeaterTurnedOn>(aggregate.Store.Events[1].Event);
    }

    [Fact]
    public void RecordReading_AboveTarget_WhileHeating_TurnsHeaterOff()
    {
        var aggregate = CreateAggregate();

        // Turn heater on first
        aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));

        // Temperature rises above target
        var result = aggregate.Handle(new ThermostatCommand.RecordReading(23.0m));

        Assert.True(result.IsOk);
        Assert.Equal(23.0m, result.Value.CurrentTemp);
        Assert.False(result.Value.Heating);
    }

    [Fact]
    public void RecordReading_AtTarget_WhileNotHeating_NoHeaterChange()
    {
        var aggregate = CreateAggregate();

        var result = aggregate.Handle(new ThermostatCommand.RecordReading(22.0m));

        Assert.True(result.IsOk);
        Assert.False(result.Value.Heating);

        // Only one event: TemperatureRecorded (no heater change)
        Assert.Single(aggregate.Store.Events);
        Assert.IsType<ThermostatEvent.TemperatureRecorded>(aggregate.Store.Events[0].Event);
    }

    [Fact]
    public void RecordReading_AboveAlertThreshold_RaisesAlert()
    {
        var aggregate = CreateAggregate();

        var result = aggregate.Handle(
            new ThermostatCommand.RecordReading(Thermostat.AlertThreshold + 1));

        Assert.True(result.IsOk);

        // Two events: TemperatureRecorded + AlertRaised
        Assert.Equal(2, aggregate.Store.Events.Count);
        Assert.IsType<ThermostatEvent.TemperatureRecorded>(aggregate.Store.Events[0].Event);
        var alert = Assert.IsType<ThermostatEvent.AlertRaised>(aggregate.Store.Events[1].Event);
        Assert.Contains("exceeds alert threshold", alert.Message);
    }

    [Fact]
    public void RecordReading_AboveAlertThreshold_WhileHeating_TurnsOffAndAlerts()
    {
        var aggregate = CreateAggregate();

        // Start heating
        aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));
        Assert.True(aggregate.State.Heating);

        // Danger zone
        var result = aggregate.Handle(
            new ThermostatCommand.RecordReading(Thermostat.AlertThreshold + 1));

        Assert.True(result.IsOk);
        Assert.False(result.Value.Heating);

        // Three events from this command: TemperatureRecorded + HeaterTurnedOff + AlertRaised
        var lastThree = aggregate.Store.Events.TakeLast(3).ToList();
        Assert.IsType<ThermostatEvent.TemperatureRecorded>(lastThree[0].Event);
        Assert.IsType<ThermostatEvent.HeaterTurnedOff>(lastThree[1].Event);
        Assert.IsType<ThermostatEvent.AlertRaised>(lastThree[2].Event);
    }

    // =========================================================================
    // Aggregate — SetTarget
    // =========================================================================

    [Fact]
    public void SetTarget_ValidTarget_UpdatesTarget()
    {
        var aggregate = CreateAggregate();

        var result = aggregate.Handle(new ThermostatCommand.SetTarget(25.0m));

        Assert.True(result.IsOk);
        Assert.Equal(25.0m, result.Value.TargetTemp);
    }

    [Fact]
    public void SetTarget_AboveCurrentTemp_TurnsHeaterOn()
    {
        var aggregate = CreateAggregate();

        // Current temp is 20, set target to 25 → heater should start
        var result = aggregate.Handle(new ThermostatCommand.SetTarget(25.0m));

        Assert.True(result.IsOk);
        Assert.True(result.Value.Heating);
        Assert.Equal(2, aggregate.Store.Events.Count);
        Assert.IsType<ThermostatEvent.TargetSet>(aggregate.Store.Events[0].Event);
        Assert.IsType<ThermostatEvent.HeaterTurnedOn>(aggregate.Store.Events[1].Event);
    }

    [Fact]
    public void SetTarget_BelowCurrentTemp_WhileHeating_TurnsHeaterOff()
    {
        var aggregate = CreateAggregate();

        // Start heating
        aggregate.Handle(new ThermostatCommand.SetTarget(25.0m));
        Assert.True(aggregate.State.Heating);

        // Lower target below current temp
        aggregate.Handle(new ThermostatCommand.SetTarget(18.0m));
        Assert.False(aggregate.State.Heating);
    }

    [Fact]
    public void SetTarget_BelowMinimum_ReturnsInvalidTargetError()
    {
        var aggregate = CreateAggregate();

        var result = aggregate.Handle(
            new ThermostatCommand.SetTarget(Thermostat.MinTarget - 1));

        Assert.True(result.IsErr);
        var invalid = Assert.IsType<ThermostatError.InvalidTarget>(result.Error);
        Assert.Equal(Thermostat.MinTarget - 1, invalid.Target);
        Assert.Equal(Thermostat.MinTarget, invalid.Min);
        Assert.Equal(Thermostat.MaxTarget, invalid.Max);
    }

    [Fact]
    public void SetTarget_AboveMaximum_ReturnsInvalidTargetError()
    {
        var aggregate = CreateAggregate();

        var result = aggregate.Handle(
            new ThermostatCommand.SetTarget(Thermostat.MaxTarget + 1));

        Assert.True(result.IsErr);
        Assert.IsType<ThermostatError.InvalidTarget>(result.Error);
    }

    [Fact]
    public void SetTarget_InvalidTarget_NothingPersisted()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.SetTarget(Thermostat.MaxTarget + 1));

        Assert.Empty(aggregate.Store.Events);
        Assert.Equal(22.0m, aggregate.State.TargetTemp); // unchanged
    }

    // =========================================================================
    // Aggregate — Shutdown
    // =========================================================================

    [Fact]
    public void Shutdown_Active_ProducesShutdownEvent()
    {
        var aggregate = CreateAggregate();

        var result = aggregate.Handle(new ThermostatCommand.Shutdown());

        Assert.True(result.IsOk);
        Assert.False(result.Value.Active);
        Assert.True(aggregate.IsTerminal);
    }

    [Fact]
    public void Shutdown_WhileHeating_TurnsOffHeaterFirst()
    {
        var aggregate = CreateAggregate();

        // Start heating
        aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));
        Assert.True(aggregate.State.Heating);

        // Shutdown should turn off heater first
        aggregate.Handle(new ThermostatCommand.Shutdown());

        Assert.False(aggregate.State.Heating);
        Assert.False(aggregate.State.Active);

        // Last two events: HeaterTurnedOff + ShutdownCompleted
        var lastTwo = aggregate.Store.Events.TakeLast(2).ToList();
        Assert.IsType<ThermostatEvent.HeaterTurnedOff>(lastTwo[0].Event);
        Assert.IsType<ThermostatEvent.ShutdownCompleted>(lastTwo[1].Event);
    }

    [Fact]
    public void Shutdown_AlreadyShutdown_ReturnsError()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.Shutdown());

        var result = aggregate.Handle(new ThermostatCommand.Shutdown());

        Assert.True(result.IsErr);
        Assert.IsType<ThermostatError.AlreadyShutdown>(result.Error);
    }

    [Fact]
    public void Shutdown_ThenCommand_ReturnsSystemInactive()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.Shutdown());

        var result = aggregate.Handle(new ThermostatCommand.RecordReading(21.0m));

        Assert.True(result.IsErr);
        Assert.IsType<ThermostatError.SystemInactive>(result.Error);
    }

    // =========================================================================
    // Event Store — Sequence Numbers
    // =========================================================================

    [Fact]
    public void EventStore_SequenceNumbers_AreMonotonicallyIncreasing()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.RecordReading(18.0m)); // 2 events
        aggregate.Handle(new ThermostatCommand.RecordReading(23.0m)); // 2 events

        for (var i = 0; i < aggregate.Store.Events.Count; i++)
        {
            Assert.Equal(i + 1, aggregate.Store.Events[i].SequenceNumber);
        }
    }

    // =========================================================================
    // Rebuild — State Reconstruction from Events
    // =========================================================================

    [Fact]
    public void Rebuild_ReconstructsStateFromEventStream()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));  // heater on
        aggregate.Handle(new ThermostatCommand.SetTarget(25.0m));      // target up
        aggregate.Handle(new ThermostatCommand.RecordReading(26.0m));  // heater off

        var stateBeforeRebuild = aggregate.State;

        // Rebuild from scratch
        var rebuilt = aggregate.Rebuild();

        Assert.Equal(stateBeforeRebuild, rebuilt);
        Assert.Equal(26.0m, rebuilt.CurrentTemp);
        Assert.Equal(25.0m, rebuilt.TargetTemp);
        Assert.False(rebuilt.Heating);
    }

    // =========================================================================
    // Hydration — Loading from Existing Store
    // =========================================================================

    [Fact]
    public void FromStore_HydratesAggregate_FromExistingEventStore()
    {
        // Simulate events loaded from a database
        var store = new EventStore<ThermostatEvent>();
        store.Append(new ThermostatEvent.TemperatureRecorded(18.0m));
        store.Append(new ThermostatEvent.HeaterTurnedOn());
        store.Append(new ThermostatEvent.TemperatureRecorded(23.0m));
        store.Append(new ThermostatEvent.HeaterTurnedOff());

        var aggregate = AggregateRunner<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.FromStore(store);

        Assert.Equal(23.0m, aggregate.State.CurrentTemp);
        Assert.False(aggregate.State.Heating);
        Assert.True(aggregate.State.Active);
    }

    [Fact]
    public void FromStore_HydratedAggregate_CanHandleMoreCommands()
    {
        var store = new EventStore<ThermostatEvent>();
        store.Append(new ThermostatEvent.TemperatureRecorded(18.0m));
        store.Append(new ThermostatEvent.HeaterTurnedOn());

        var aggregate = AggregateRunner<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.FromStore(store);

        // Continue processing
        aggregate.Handle(new ThermostatCommand.RecordReading(23.0m));

        Assert.Equal(23.0m, aggregate.State.CurrentTemp);
        Assert.False(aggregate.State.Heating);
        Assert.Equal(4, aggregate.Store.Events.Count); // 2 hydrated + 2 new
    }

    [Fact]
    public void FromStore_WithShutdown_HydratesTerminalState()
    {
        var store = new EventStore<ThermostatEvent>();
        store.Append(new ThermostatEvent.ShutdownCompleted());

        var aggregate = AggregateRunner<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.FromStore(store);

        Assert.True(aggregate.IsTerminal);
        Assert.False(aggregate.State.Active);
    }

    // =========================================================================
    // Projections — Read Models from Event Stream
    // =========================================================================

    [Fact]
    public void Projection_TemperatureHistory_TracksAllReadings()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));
        aggregate.Handle(new ThermostatCommand.RecordReading(19.5m));
        aggregate.Handle(new ThermostatCommand.RecordReading(21.0m));
        aggregate.Handle(new ThermostatCommand.RecordReading(22.5m));

        // Projection: collect all temperature readings
        var tempHistory = new Projection<ThermostatEvent, List<decimal>>(
            initial: [],
            apply: (history, @event) =>
            {
                if (@event is ThermostatEvent.TemperatureRecorded(var temp))
                    history.Add(temp);
                return history;
            });

        var readings = tempHistory.Project(aggregate.Store);

        Assert.Equal([18.0m, 19.5m, 21.0m, 22.5m], readings);

        // State only remembers the latest temperature
        Assert.Equal(22.5m, aggregate.State.CurrentTemp);
    }

    [Fact]
    public void Projection_HeaterDutyCycle_CountsOnOffCycles()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));  // heater on
        aggregate.Handle(new ThermostatCommand.RecordReading(23.0m));  // heater off
        aggregate.Handle(new ThermostatCommand.RecordReading(19.0m));  // heater on
        aggregate.Handle(new ThermostatCommand.RecordReading(23.0m));  // heater off

        // Projection: count heater duty cycles (on/off pairs)
        var dutyCycle = new Projection<ThermostatEvent, (int OnCount, int OffCount)>(
            initial: (0, 0),
            apply: (counts, @event) => @event switch
            {
                ThermostatEvent.HeaterTurnedOn => (counts.OnCount + 1, counts.OffCount),
                ThermostatEvent.HeaterTurnedOff => (counts.OnCount, counts.OffCount + 1),
                _ => counts
            });

        var cycles = dutyCycle.Project(aggregate.Store);

        Assert.Equal(2, cycles.OnCount);
        Assert.Equal(2, cycles.OffCount);

        // State only knows current heating status
        Assert.False(aggregate.State.Heating);
    }

    [Fact]
    public void Projection_AlertLog_CollectsAllAlerts()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.RecordReading(36.0m));
        aggregate.Handle(new ThermostatCommand.RecordReading(21.0m));
        aggregate.Handle(new ThermostatCommand.RecordReading(37.0m));

        // Projection: collect all alert messages
        var alertLog = new Projection<ThermostatEvent, List<string>>(
            initial: [],
            apply: (log, @event) =>
            {
                if (@event is ThermostatEvent.AlertRaised(var message))
                    log.Add(message);
                return log;
            });

        var alerts = alertLog.Project(aggregate.Store);

        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Contains("exceeds alert threshold", a));

        // State doesn't track alert history — projections do
    }

    [Fact]
    public void Projection_AuditLog_TracksAllEventTypes()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));
        aggregate.Handle(new ThermostatCommand.SetTarget(25.0m));
        aggregate.Handle(new ThermostatCommand.Shutdown());

        var auditLog = new Projection<ThermostatEvent, List<string>>(
            initial: [],
            apply: (log, @event) =>
            {
                log.Add(@event.GetType().Name);
                return log;
            });

        var log = auditLog.Project(aggregate.Store);

        Assert.Equal(
            ["TemperatureRecorded", "HeaterTurnedOn",   // RecordReading(18)
             "TargetSet",                                // SetTarget(25) — already heating
             "HeaterTurnedOff", "ShutdownCompleted"],    // Shutdown
            log);
    }

    // =========================================================================
    // Effects — Recorded for Later Processing
    // =========================================================================

    [Fact]
    public void Effects_AreRecorded_ForLaterProcessing()
    {
        var aggregate = CreateAggregate();

        aggregate.Handle(new ThermostatCommand.RecordReading(18.0m)); // heater on
        aggregate.Handle(new ThermostatCommand.RecordReading(36.0m)); // alert + heater off

        Assert.Contains(aggregate.Effects,
            e => e is ThermostatEffect.ActivateHeater);
        Assert.Contains(aggregate.Effects,
            e => e is ThermostatEffect.DeactivateHeater);
        Assert.Contains(aggregate.Effects,
            e => e is ThermostatEffect.SendNotification);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static AggregateRunner<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit> CreateAggregate() =>
        AggregateRunner<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Create();
}
