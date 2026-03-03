// =============================================================================
// Actor Runtime Tests
// =============================================================================
// Proves the SAME thermostat automaton works as a mailbox-based actor:
// messages arrive via channel, are processed sequentially, effects handled.
// =============================================================================

using Automaton.Actor;

namespace Automaton.Tests;

public class ActorTests
{
    [Fact]
    public async Task Actor_InitialState_IsDefault()
    {
        var actor = ActorInstance<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .Spawn("thermostat");

        Assert.Equal(20m, actor.State.CurrentTemp);
        Assert.Equal(22m, actor.State.TargetTemp);
        Assert.False(actor.State.Heating);
        Assert.True(actor.State.Active);

        await actor.Stop();
    }

    [Fact]
    public async Task Actor_ProcessesMessages_FromMailbox()
    {
        var actor = ActorInstance<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .Spawn("thermostat");

        await actor.Ref.Tell(new ThermostatEvent.TemperatureRecorded(18m));
        await actor.Ref.Tell(new ThermostatEvent.HeaterTurnedOn());
        await actor.Ref.Tell(new ThermostatEvent.TemperatureRecorded(23m));

        await actor.DrainMailbox();

        Assert.Equal(23m, actor.State.CurrentTemp);
        Assert.True(actor.State.Heating);
        Assert.Equal(3, actor.ProcessedMessages.Count);

        await actor.Stop();
    }

    [Fact]
    public async Task Actor_ProcessesMessages_Sequentially()
    {
        var actor = ActorInstance<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .Spawn("thermostat");

        // Send many messages concurrently
        var tasks = Enumerable.Range(0, 100)
            .Select(i => actor.Ref.Tell(new ThermostatEvent.TemperatureRecorded(15m)).AsTask());
        await Task.WhenAll(tasks);

        await actor.DrainMailbox();

        // Sequential processing guarantees correct final state
        Assert.Equal(15m, actor.State.CurrentTemp);
        Assert.Equal(100, actor.ProcessedMessages.Count);

        await actor.Stop();
    }

    [Fact]
    public async Task Actor_HandlesEffects_ViaCallback()
    {
        var notifications = new List<string>();

        var actor = ActorInstance<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>.Spawn(
            "thermostat",
            effectHandler: (effect, self) =>
            {
                if (effect is ThermostatEffect.SendNotification notification)
                {
                    notifications.Add(notification.Message);
                }

                return Task.CompletedTask;
            });

        await actor.Ref.Tell(new ThermostatEvent.ShutdownCompleted());

        await actor.DrainMailbox();

        Assert.False(actor.State.Active);
        Assert.Single(notifications);
        Assert.Equal("Thermostat shut down", notifications[0]);

        await actor.Stop();
    }

    [Fact]
    public async Task ActorRef_HasName()
    {
        var actor = ActorInstance<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .Spawn("my-thermostat");

        Assert.Equal("my-thermostat", actor.Ref.Name);

        await actor.Stop();
    }

    [Fact]
    public async Task Actor_HeaterCycle_ProducesCorrectState()
    {
        var actor = ActorInstance<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit>
            .Spawn("thermostat");

        await actor.Ref.Tell(new ThermostatEvent.TemperatureRecorded(18m));
        await actor.Ref.Tell(new ThermostatEvent.HeaterTurnedOn());
        await actor.Ref.Tell(new ThermostatEvent.TemperatureRecorded(23m));
        await actor.Ref.Tell(new ThermostatEvent.HeaterTurnedOff());

        await actor.DrainMailbox();

        Assert.Equal(23m, actor.State.CurrentTemp);
        Assert.False(actor.State.Heating);

        await actor.Stop();
    }
}
