// =============================================================================
// Actor Tests — Hewitt's Three Axioms on the Automaton Kernel
// =============================================================================
// These tests prove the actor primitives correctly implement Hewitt's 1973 model:
//
//     Axiom 1: Send messages to other actors         → Tell tests
//     Axiom 2: Create new actors                     → Spawn tests
//     Axiom 3: Designate behavior for next message   → Transition tests
//
// Additional properties from Hewitt's model:
//     - Locality: can only send to addresses you possess
//     - Sequential processing: one message at a time
//     - Encapsulation: state is internal to the actor
//     - Terminal behavior: actors can stop processing
//     - Variable topology: addresses can be shared between actors
//
// All tests use Decider-based actors (Counter and Thermostat) because
// all actors in this system are Deciders — a domain design choice.
// =============================================================================

using Automaton.Actor;
using Automaton.Actor.Testing;

namespace Automaton.Tests;

public class ActorTests
{
    // =========================================================================
    // Axiom 2: Create new actors
    // =========================================================================

    [Fact]
    public async Task Spawn_creates_actor_with_initial_state()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        Assert.Equal(0, actor.State.Count);

        actor.Stop();
    }

    [Fact]
    public async Task Spawn_returns_address_capability()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        Assert.NotNull(actor.Address);

        actor.Stop();
    }

    [Fact]
    public async Task Spawn_creates_distinct_actors()
    {
        var actor1 = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);
        var actor2 = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        await actor1.Address.Tell(new CounterCommand.Add(5));
        await actor1.Drain();

        Assert.Equal(5, actor1.State.Count);
        Assert.Equal(0, actor2.State.Count);

        actor1.Stop();
        actor2.Stop();
    }

    // =========================================================================
    // Axiom 1: Send messages to other actors
    // =========================================================================

    [Fact]
    public async Task Tell_sends_command_to_actor()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        await actor.Address.Tell(new CounterCommand.Add(3));
        await actor.Drain();

        Assert.Equal(3, actor.State.Count);

        actor.Stop();
    }

    [Fact]
    public async Task Tell_is_fire_and_forget()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        // Tell completes immediately — it does not wait for the command to be processed
        await actor.Address.Tell(new CounterCommand.Add(1));
        await actor.Address.Tell(new CounterCommand.Add(2));
        await actor.Address.Tell(new CounterCommand.Add(3));

        await actor.Drain();

        Assert.Equal(6, actor.State.Count);

        actor.Stop();
    }

    [Fact]
    public async Task Tell_applies_backpressure_when_mailbox_full()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(
                default, mailboxCapacity: 2);

        // Send more commands than mailbox capacity — should not lose messages
        for (var i = 0; i < 10; i++)
            await actor.Address.Tell(new CounterCommand.Add(1));

        await actor.Drain();

        Assert.Equal(10, actor.State.Count);

        actor.Stop();
    }

    // =========================================================================
    // Axiom 3: Designate behavior for next message
    // =========================================================================

    [Fact]
    public async Task Transition_designates_next_behavior()
    {
        var actor = await TestActor<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Spawn(default);

        // First command: set target temperature
        await actor.Address.Tell(new ThermostatCommand.SetTarget(25m));
        await actor.Drain();

        Assert.Equal(25m, actor.State.TargetTemp);

        // Second command: behavior has evolved — state now includes new target
        await actor.Address.Tell(new ThermostatCommand.RecordReading(18m));
        await actor.Drain();

        Assert.Equal(18m, actor.State.CurrentTemp);
        Assert.True(actor.State.Heating);

        actor.Stop();
    }

    [Fact]
    public async Task Decide_rejects_invalid_commands()
    {
        var actor = await TestActor<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Spawn(default);

        // Invalid target (outside 5-40 range) should be rejected
        await actor.Address.Tell(new ThermostatCommand.SetTarget(50m));
        await actor.Drain();

        Assert.Single(actor.Errors);
        Assert.IsType<ThermostatError.InvalidTarget>(actor.Errors[0]);
        Assert.Equal(22m, actor.State.TargetTemp); // unchanged

        actor.Stop();
    }

    [Fact]
    public async Task Decide_produces_events_on_valid_commands()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        await actor.Address.Tell(new CounterCommand.Add(5));
        await actor.Drain();

        Assert.Equal(5, actor.Events.Count);
        Assert.All(actor.Events, e => Assert.IsType<CounterEvent.Increment>(e));

        actor.Stop();
    }

    // =========================================================================
    // Sequential processing (Hewitt's fundamental guarantee)
    // =========================================================================

    [Fact]
    public async Task Actor_processes_commands_sequentially()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        // Send 100 concurrent commands — sequential processing guarantees deterministic outcome
        var tasks = Enumerable.Range(0, 100)
            .Select(_ => actor.Address.Tell(new CounterCommand.Add(1)).AsTask());
        await Task.WhenAll(tasks);

        await actor.Drain();

        Assert.Equal(100, actor.State.Count);

        actor.Stop();
    }

    // =========================================================================
    // Terminal behavior
    // =========================================================================

    [Fact]
    public async Task Actor_stops_on_terminal_state()
    {
        var actor = await TestActor<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Spawn(default);

        // Shutdown command puts the thermostat into terminal state
        await actor.Address.Tell(new ThermostatCommand.Shutdown());
        await actor.Drain();

        Assert.False(actor.State.Active);

        actor.Stop();
    }

    // =========================================================================
    // Effect handling
    // =========================================================================

    [Fact]
    public async Task Actor_handles_effects_via_interpreter()
    {
        var notifications = new List<string>();

        Interpreter<ThermostatEffect, ThermostatEvent> interpreter = effect =>
        {
            if (effect is ThermostatEffect.SendNotification notification)
                notifications.Add(notification.Message);

            return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                Result<ThermostatEvent[], PipelineError>.Ok([]));
        };

        var actor = await TestActor<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Spawn(
                default, interpreter: interpreter);

        await actor.Address.Tell(new ThermostatCommand.Shutdown());
        await actor.Drain();

        Assert.Single(notifications);
        Assert.Equal("Thermostat shut down", notifications[0]);

        actor.Stop();
    }

    [Fact]
    public async Task Actor_produces_effects_on_transitions()
    {
        var actor = await TestActor<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Spawn(default);

        // Recording a low temperature should produce ActivateHeater effect
        await actor.Address.Tell(new ThermostatCommand.RecordReading(18m));
        await actor.Drain();

        Assert.Contains(actor.Effects, e => e is ThermostatEffect.ActivateHeater);

        actor.Stop();
    }

    // =========================================================================
    // Production Spawn (no test observability)
    // =========================================================================

    [Fact]
    public async Task Production_spawn_returns_opaque_address()
    {
        using var cts = new CancellationTokenSource();

        Observer<CounterState, CounterEvent, CounterEffect> observer =
            (_, _, _) => PipelineResult.Ok;

        Interpreter<CounterEffect, CounterEvent> interpreter =
            _ => new ValueTask<Result<CounterEvent[], PipelineError>>(
                Result<CounterEvent[], PipelineError>.Ok([]));

        var address = await Automaton.Actor.Actor.Spawn<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>(
                default, observer, interpreter, cancellationToken: cts.Token);

        Assert.NotNull(address);

        // Can send commands via address
        await address.Tell(new CounterCommand.Add(1));

        // Small delay to let the processing loop handle the message
        await Task.Delay(50);

        await cts.CancelAsync();
    }

    // =========================================================================
    // Heater cycle (domain integration — proves the full cycle works)
    // =========================================================================

    [Fact]
    public async Task Heater_cycle_produces_correct_state()
    {
        var actor = await TestActor<Thermostat, ThermostatState, ThermostatCommand,
            ThermostatEvent, ThermostatEffect, ThermostatError, Unit>.Spawn(default);

        await actor.Address.Tell(new ThermostatCommand.RecordReading(18m));
        await actor.Drain();

        Assert.Equal(18m, actor.State.CurrentTemp);
        Assert.True(actor.State.Heating);

        await actor.Address.Tell(new ThermostatCommand.RecordReading(23m));
        await actor.Drain();

        Assert.Equal(23m, actor.State.CurrentTemp);
        Assert.False(actor.State.Heating);

        actor.Stop();
    }

    // =========================================================================
    // Counter domain — decrement and errors
    // =========================================================================

    [Fact]
    public async Task Counter_rejects_overflow()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        // Counter max is 100, adding 101 should be rejected
        await actor.Address.Tell(new CounterCommand.Add(101));
        await actor.Drain();

        Assert.Single(actor.Errors);
        Assert.IsType<CounterError.Overflow>(actor.Errors[0]);
        Assert.Equal(0, actor.State.Count);

        actor.Stop();
    }

    [Fact]
    public async Task Counter_rejects_underflow()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        // Subtracting from zero should be rejected
        await actor.Address.Tell(new CounterCommand.Add(-1));
        await actor.Drain();

        Assert.Single(actor.Errors);
        Assert.IsType<CounterError.Underflow>(actor.Errors[0]);
        Assert.Equal(0, actor.State.Count);

        actor.Stop();
    }

    [Fact]
    public async Task Counter_reset_when_already_zero_is_rejected()
    {
        var actor = await TestActor<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Spawn(default);

        await actor.Address.Tell(new CounterCommand.Reset());
        await actor.Drain();

        Assert.Single(actor.Errors);
        Assert.IsType<CounterError.AlreadyAtZero>(actor.Errors[0]);

        actor.Stop();
    }
}
