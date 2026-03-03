// =============================================================================
// AggregateRunner Tests
// =============================================================================

using Automaton.Patterns.EventSourcing;
using Automaton.Patterns.Tests.TestDomain;

namespace Automaton.Patterns.Tests.EventSourcing;

public class AggregateRunnerTests : IDisposable
{
    private readonly InMemoryEventStore<CounterEvent> _store = new();
    private const string _streamId = "counter-1";
    private AggregateRunner<CounterDecider, CounterState, CounterCommand,
        CounterEvent, CounterEffect, CounterError, Unit>? _aggregate;

    public void Dispose() => _aggregate?.Dispose();

    private AggregateRunner<CounterDecider, CounterState, CounterCommand,
        CounterEvent, CounterEffect, CounterError, Unit> CreateAggregate() =>
        _aggregate = AggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Create(_store, _streamId, default);

    private async ValueTask<AggregateRunner<CounterDecider, CounterState, CounterCommand,
        CounterEvent, CounterEffect, CounterError, Unit>> LoadAggregate() =>
        _aggregate = await AggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);

    // ── Create ──

    [Fact]
    public void Create_InitializesWithDefaultState()
    {
        var aggregate = CreateAggregate();

        Assert.Equal(0, aggregate.State.Count);
        Assert.Equal(0, aggregate.Version);
        Assert.Equal(_streamId, aggregate.StreamId);
        Assert.Empty(aggregate.Effects);
        Assert.False(aggregate.IsTerminal);
    }

    // ── Handle: success ──

    [Fact]
    public async Task Handle_ValidCommand_TransitionsStateAndPersists()
    {
        var aggregate = CreateAggregate();

        var result = await aggregate.Handle(new CounterCommand.Add(3));

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value.Count);
        Assert.Equal(3, aggregate.State.Count);
        Assert.Equal(3, aggregate.Version);

        // Verify events persisted
        var stream = _store.GetStream(_streamId);
        Assert.Equal(3, stream.Count);
        Assert.All(stream, e => Assert.IsType<CounterEvent.Incremented>(e.Event));
    }

    [Fact]
    public async Task Handle_MultipleCommands_AccumulatesState()
    {
        var aggregate = CreateAggregate();

        await aggregate.Handle(new CounterCommand.Add(5));
        await aggregate.Handle(new CounterCommand.Add(-2));

        Assert.Equal(3, aggregate.State.Count);
        // Add(5) => 5 Incremented events, Add(-2) => 2 Decremented events = 7 total
        Assert.Equal(7, aggregate.Version);
    }

    [Fact]
    public async Task Handle_Reset_ProducesEffects()
    {
        var aggregate = CreateAggregate();

        await aggregate.Handle(new CounterCommand.Add(5));
        await aggregate.Handle(new CounterCommand.Reset());

        Assert.Equal(0, aggregate.State.Count);
        // 5 None effects + 1 Log effect
        Assert.Equal(6, aggregate.Effects.Count);
        Assert.IsType<CounterEffect.Log>(aggregate.Effects[^1]);
    }

    // ── Handle: error ──

    [Fact]
    public async Task Handle_InvalidCommand_ReturnsErrorAndDoesNotPersist()
    {
        var aggregate = CreateAggregate();

        var result = await aggregate.Handle(new CounterCommand.Add(101));

        Assert.True(result.IsErr);
        Assert.IsType<CounterError.Overflow>(result.Error);
        Assert.Equal(0, aggregate.State.Count);
        Assert.Equal(0, aggregate.Version);
        Assert.Empty(_store.GetStream(_streamId));
    }

    [Fact]
    public async Task Handle_Underflow_ReturnsError()
    {
        var aggregate = CreateAggregate();

        var result = await aggregate.Handle(new CounterCommand.Add(-1));

        Assert.True(result.IsErr);
        Assert.IsType<CounterError.Underflow>(result.Error);
    }

    [Fact]
    public async Task Handle_ResetWhenZero_ReturnsError()
    {
        var aggregate = CreateAggregate();

        var result = await aggregate.Handle(new CounterCommand.Reset());

        Assert.True(result.IsErr);
        Assert.IsType<CounterError.AlreadyAtZero>(result.Error);
    }

    // ── Handle: idempotent command (Add 0) ──

    [Fact]
    public async Task Handle_IdempotentCommand_ReturnsOkWithNoEvents()
    {
        var aggregate = CreateAggregate();

        var result = await aggregate.Handle(new CounterCommand.Add(0));

        Assert.True(result.IsOk);
        Assert.Equal(0, aggregate.Version);
        Assert.Empty(_store.GetStream(_streamId));
    }

    // ── Load / Replay ──

    [Fact]
    public async Task Load_ReplaysEventsToRebuildState()
    {
        // Arrange: persist events
        var original = CreateAggregate();
        await original.Handle(new CounterCommand.Add(5));
        await original.Handle(new CounterCommand.Add(-2));
        original.Dispose();
        _aggregate = null;

        // Act: reload from store
        var reloaded = await LoadAggregate();

        Assert.Equal(3, reloaded.State.Count);
        Assert.Equal(7, reloaded.Version);
    }

    [Fact]
    public async Task Load_EmptyStream_ReturnsInitialState()
    {
        var aggregate = await LoadAggregate();

        Assert.Equal(0, aggregate.State.Count);
        Assert.Equal(0, aggregate.Version);
    }

    [Fact]
    public async Task Load_ThenHandleMore_ContinuesFromVersion()
    {
        var original = CreateAggregate();
        await original.Handle(new CounterCommand.Add(3));
        original.Dispose();
        _aggregate = null;

        var reloaded = await LoadAggregate();
        await reloaded.Handle(new CounterCommand.Add(2));

        Assert.Equal(5, reloaded.State.Count);
        Assert.Equal(5, reloaded.Version);

        // Verify all events in store
        var stream = _store.GetStream(_streamId);
        Assert.Equal(5, stream.Count);
    }

    // ── Rebuild ──

    [Fact]
    public async Task Rebuild_ResynchronizesWithStore()
    {
        var aggregate = CreateAggregate();
        await aggregate.Handle(new CounterCommand.Add(5));

        // Directly append to store (simulating another process)
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented(), new CounterEvent.Incremented()], 5);

        // Aggregate doesn't know about the extra events
        Assert.Equal(5, aggregate.State.Count);

        // Rebuild
        var state = await aggregate.Rebuild();

        Assert.Equal(7, state.Count);
        Assert.Equal(7, aggregate.Version);
    }

    // ── Concurrency ──

    [Fact]
    public async Task Handle_ConcurrencyConflict_ThrowsAndPreservesState()
    {
        // Two aggregates pointing to the same stream
        var agg1 = CreateAggregate();
        var agg2 = AggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Create(_store, _streamId, default);

        try
        {
            // Both start at version 0
            await agg1.Handle(new CounterCommand.Add(1));

            // agg2 still thinks version is 0, but store is at version 1
            await Assert.ThrowsAsync<ConcurrencyException>(async () =>
                await agg2.Handle(new CounterCommand.Add(1)));

            // agg2 state should be unchanged (still at initial)
            Assert.Equal(0, agg2.State.Count);
            Assert.Equal(0, agg2.Version);
        }
        finally
        {
            agg2.Dispose();
        }
    }

    // ── Thread safety ──

    [Fact]
    public async Task Handle_ConcurrentCalls_SerializesCorrectly()
    {
        var aggregate = CreateAggregate();

        // Fire 10 concurrent Add(1) commands
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => aggregate.Handle(new CounterCommand.Add(1)).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All should succeed
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(10, aggregate.State.Count);
        Assert.Equal(10, aggregate.Version);
    }

    // ── Cancellation ──

    [Fact]
    public async Task Handle_CancelledToken_ThrowsOperationCancelled()
    {
        var aggregate = CreateAggregate();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await aggregate.Handle(new CounterCommand.Add(1), cts.Token));
    }
}
