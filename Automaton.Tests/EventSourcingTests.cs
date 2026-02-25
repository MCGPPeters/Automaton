// =============================================================================
// Event Sourcing Runtime Tests
// =============================================================================
// Proves the SAME counter automaton works as an event-sourced aggregate.
// Event sourcing is fundamentally command-driven: commands are validated via
// Decide, and only the resulting events are persisted.
// =============================================================================

using Automaton.EventSourcing;

namespace Automaton.Tests;

public class EventSourcingTests
{
    [Fact]
    public void Aggregate_InitialState_IsZero()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        Assert.Equal(0, aggregate.State.Count);
    }

    [Fact]
    public void Handle_ValidCommand_TransitionsState()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        var result = aggregate.Handle(new CounterCommand.Add(3));

        var ok = Assert.IsType<Result<CounterState, CounterError>.Ok>(result);
        Assert.Equal(3, ok.Value.Count);
        Assert.Equal(3, aggregate.State.Count);
    }

    [Fact]
    public void Handle_ValidCommand_PersistsEvents()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        aggregate.Handle(new CounterCommand.Add(3));

        Assert.Equal(3, aggregate.Store.Events.Count);
        Assert.All(aggregate.Store.Events, e =>
            Assert.IsType<CounterEvent.Increment>(e.Event));
    }

    [Fact]
    public void Handle_InvalidCommand_ReturnsError_StateUnchanged()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        var result = aggregate.Handle(new CounterCommand.Add(200));

        var err = Assert.IsType<Result<CounterState, CounterError>.Err>(result);
        Assert.IsType<CounterError.Overflow>(err.Error);
        Assert.Equal(0, aggregate.State.Count);
        Assert.Empty(aggregate.Store.Events);
    }

    [Fact]
    public void Handle_InvalidCommand_NothingPersisted()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        // Valid command first
        aggregate.Handle(new CounterCommand.Add(5));
        var eventCountBefore = aggregate.Store.Events.Count;

        // Invalid command — overflow
        aggregate.Handle(new CounterCommand.Add(200));

        Assert.Equal(eventCountBefore, aggregate.Store.Events.Count);
        Assert.Equal(5, aggregate.State.Count);
    }

    [Fact]
    public void Handle_SubtractCommand_ProducesDecrementEvents()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        aggregate.Handle(new CounterCommand.Add(5));
        aggregate.Handle(new CounterCommand.Add(-2));

        Assert.Equal(3, aggregate.State.Count);
        Assert.Equal(7, aggregate.Store.Events.Count); // 5 increments + 2 decrements
        Assert.IsType<CounterEvent.Decrement>(aggregate.Store.Events[5].Event);
        Assert.IsType<CounterEvent.Decrement>(aggregate.Store.Events[6].Event);
    }

    [Fact]
    public void Handle_ResetCommand_ProducesResetEvent()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        aggregate.Handle(new CounterCommand.Add(3));
        aggregate.Handle(new CounterCommand.Reset());

        Assert.Equal(0, aggregate.State.Count);
        Assert.Equal(4, aggregate.Store.Events.Count); // 3 increments + 1 reset
        Assert.IsType<CounterEvent.Reset>(aggregate.Store.Events[3].Event);
    }

    [Fact]
    public void Handle_ResetAtZero_ReturnsAlreadyAtZeroError()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        var result = aggregate.Handle(new CounterCommand.Reset());

        var err = Assert.IsType<Result<CounterState, CounterError>.Err>(result);
        Assert.IsType<CounterError.AlreadyAtZero>(err.Error);
    }

    [Fact]
    public void Handle_UnderflowCommand_ReturnsUnderflowError()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        var result = aggregate.Handle(new CounterCommand.Add(-1));

        var err = Assert.IsType<Result<CounterState, CounterError>.Err>(result);
        Assert.IsType<CounterError.Underflow>(err.Error);
    }

    [Fact]
    public void EventStore_SequenceNumbers_AreMonotonicallyIncreasing()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        aggregate.Handle(new CounterCommand.Add(3));

        Assert.Equal(1, aggregate.Store.Events[0].SequenceNumber);
        Assert.Equal(2, aggregate.Store.Events[1].SequenceNumber);
        Assert.Equal(3, aggregate.Store.Events[2].SequenceNumber);
    }

    [Fact]
    public void Rebuild_ReconstructsStateFromEventStream()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        aggregate.Handle(new CounterCommand.Add(4));
        aggregate.Handle(new CounterCommand.Add(-1));

        // Rebuild from scratch (simulates loading from disk)
        var rebuilt = aggregate.Rebuild();

        Assert.Equal(3, rebuilt.Count);
        Assert.Equal(aggregate.State, rebuilt);
    }

    [Fact]
    public void FromStore_HydratesAggregate_FromExistingEventStore()
    {
        // Simulate: events were persisted to a store previously
        var store = new EventStore<CounterEvent>();
        store.Append(new CounterEvent.Increment());
        store.Append(new CounterEvent.Increment());
        store.Append(new CounterEvent.Increment());
        store.Append(new CounterEvent.Decrement());

        // Hydrate a new aggregate from the existing store
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.FromStore(store);

        Assert.Equal(2, aggregate.State.Count);
    }

    [Fact]
    public void FromStore_HydratedAggregate_CanHandleMoreCommands()
    {
        var store = new EventStore<CounterEvent>();
        store.Append(new CounterEvent.Increment());
        store.Append(new CounterEvent.Increment());

        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.FromStore(store);

        aggregate.Handle(new CounterCommand.Add(3));

        Assert.Equal(5, aggregate.State.Count);
        Assert.Equal(5, aggregate.Store.Events.Count); // 2 hydrated + 3 new
    }

    [Fact]
    public void Projection_BuildsReadModel_FromEventStream()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        aggregate.Handle(new CounterCommand.Add(2));
        aggregate.Handle(new CounterCommand.Reset());
        aggregate.Handle(new CounterCommand.Add(1));

        // Projection: count total number of increments (different from current state!)
        var incrementCount = new Projection<CounterEvent, int>(
            initial: 0,
            apply: (count, @event) => @event is CounterEvent.Increment ? count + 1 : count);

        var totalIncrements = incrementCount.Project(aggregate.Store);

        // State is 1 (inc, inc, reset, inc) but total increments is 3
        Assert.Equal(1, aggregate.State.Count);
        Assert.Equal(3, totalIncrements);
    }

    [Fact]
    public void Projection_BuildsAuditLog_FromEventStream()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        aggregate.Handle(new CounterCommand.Add(1));
        aggregate.Handle(new CounterCommand.Add(-1));
        aggregate.Handle(new CounterCommand.Add(1));
        aggregate.Handle(new CounterCommand.Reset());

        // Projection: build an audit log of all event types
        var auditLog = new Projection<CounterEvent, List<string>>(
            initial: [],
            apply: (log, @event) =>
            {
                log.Add(@event.GetType().Name);
                return log;
            });

        var log = auditLog.Project(aggregate.Store);

        Assert.Equal(["Increment", "Decrement", "Increment", "Reset"], log);
    }

    [Fact]
    public void Effects_AreRecorded()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        aggregate.Handle(new CounterCommand.Add(1));
        aggregate.Handle(new CounterCommand.Reset());

        Assert.Equal(2, aggregate.Effects.Count);
        Assert.IsType<CounterEffect.None>(aggregate.Effects[0]);
        Assert.IsType<CounterEffect.Log>(aggregate.Effects[1]);
    }

    [Fact]
    public void IsTerminal_DefaultsFalse()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        Assert.False(aggregate.IsTerminal);
    }

    [Fact]
    public void Handle_Synchronous_NoAsyncOverhead()
    {
        // Handle is synchronous — no Task allocation, no async state machine.
        // This is appropriate for ES where Decide and Transition are pure.
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        Result<CounterState, CounterError> result = aggregate.Handle(new CounterCommand.Add(5));

        Assert.IsType<Result<CounterState, CounterError>.Ok>(result);
    }
}
