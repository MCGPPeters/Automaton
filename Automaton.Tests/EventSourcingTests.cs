// =============================================================================
// Event Sourcing Runtime Tests
// =============================================================================
// Proves the SAME counter automaton works as an event-sourced aggregate:
// events are stored, state is rebuilt from the stream, projections work.
// =============================================================================

using Automaton.EventSourcing;

namespace Automaton.Tests;

public class EventSourcingTests
{
    [Fact]
    public void Aggregate_InitialState_IsZero()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        Assert.Equal(0, aggregate.State.Count);
    }

    [Fact]
    public async Task Aggregate_DispatchEvents_UpdatesState()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Increment());

        Assert.Equal(3, aggregate.State.Count);
    }

    [Fact]
    public async Task EventStore_PersistsAllEvents()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Decrement());
        await aggregate.Dispatch(new CounterEvent.Reset());

        Assert.Equal(3, aggregate.Store.Events.Count);
        Assert.IsType<CounterEvent.Increment>(aggregate.Store.Events[0].Event);
        Assert.IsType<CounterEvent.Decrement>(aggregate.Store.Events[1].Event);
        Assert.IsType<CounterEvent.Reset>(aggregate.Store.Events[2].Event);
    }

    [Fact]
    public async Task EventStore_SequenceNumbers_AreMonotonicallyIncreasing()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Increment());

        Assert.Equal(1, aggregate.Store.Events[0].SequenceNumber);
        Assert.Equal(2, aggregate.Store.Events[1].SequenceNumber);
        Assert.Equal(3, aggregate.Store.Events[2].SequenceNumber);
    }

    [Fact]
    public async Task Rebuild_ReconstructsStateFromEventStream()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Decrement());

        // Rebuild from scratch (simulates loading from disk)
        var rebuilt = aggregate.Rebuild();

        Assert.Equal(2, rebuilt.Count);
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
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.FromStore(store);

        Assert.Equal(2, aggregate.State.Count);
    }

    [Fact]
    public async Task Projection_BuildsReadModel_FromEventStream()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Reset());
        await aggregate.Dispatch(new CounterEvent.Increment());

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
    public async Task Projection_BuildsAuditLog_FromEventStream()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Decrement());
        await aggregate.Dispatch(new CounterEvent.Reset());

        // Projection: build an audit log of all event types
        var auditLog = new Projection<CounterEvent, List<string>>(
            initial: [],
            apply: (log, @event) =>
            {
                log.Add(@event.GetType().Name);
                return log;
            });

        var log = auditLog.Project(aggregate.Store);

        Assert.Equal(["Increment", "Decrement", "Reset"], log);
    }

    [Fact]
    public async Task Effects_AreRecorded()
    {
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        await aggregate.Dispatch(new CounterEvent.Increment());
        await aggregate.Dispatch(new CounterEvent.Reset());

        Assert.Equal(2, aggregate.Effects.Count);
        Assert.IsType<CounterEffect.None>(aggregate.Effects[0]);
        Assert.IsType<CounterEffect.Log>(aggregate.Effects[1]);
    }
}
