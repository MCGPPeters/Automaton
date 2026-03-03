// =============================================================================
// ResolvingAggregateRunner Tests
// =============================================================================
// Tests the automatic conflict resolution retry loop in
// ResolvingAggregateRunner, using CounterDecider (which implements
// ConflictResolver) and the InMemoryEventStore.
// =============================================================================

using Automaton.Patterns.EventSourcing;
using Automaton.Patterns.Tests.TestDomain;

namespace Automaton.Patterns.Tests.EventSourcing;

public class ResolvingAggregateRunnerTests : IDisposable
{
    private readonly InMemoryEventStore<CounterEvent> _store = new();
    private const string _streamId = "counter-1";
    private ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
        CounterEvent, CounterEffect, CounterError, Unit>? _aggregate;

    public void Dispose() => _aggregate?.Dispose();

    private ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
        CounterEvent, CounterEffect, CounterError, Unit> CreateAggregate() =>
        _aggregate = ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Create(_store, _streamId, default);

    private async ValueTask<ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
        CounterEvent, CounterEffect, CounterError, Unit>> LoadAggregate() =>
        _aggregate = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);

    // ── Basic Handle (no conflict) ──

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

    [Fact]
    public async Task Handle_ValidCommand_TransitionsAndPersists()
    {
        var aggregate = CreateAggregate();

        var result = await aggregate.Handle(new CounterCommand.Add(3));

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value.Count);
        Assert.Equal(3, aggregate.Version);

        var stream = _store.GetStream(_streamId);
        Assert.Equal(3, stream.Count);
    }

    [Fact]
    public async Task Handle_InvalidCommand_ReturnsError()
    {
        var aggregate = CreateAggregate();

        var result = await aggregate.Handle(new CounterCommand.Add(101));

        Assert.True(result.IsErr);
        Assert.IsType<CounterError.Overflow>(result.Error);
        Assert.Equal(0, aggregate.State.Count);
        Assert.Equal(0, aggregate.Version);
    }

    // ── Conflict Resolution: Commutative Operations ──

    [Fact]
    public async Task Handle_CommutativeConflict_ResolvesAutomatically()
    {
        // Two aggregates pointing to the same stream
        var agg1 = CreateAggregate();
        using var agg2 = ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Create(_store, _streamId, default);

        // agg1 adds 3 (version 0 → 3)
        await agg1.Handle(new CounterCommand.Add(3));

        // agg2 still at version 0, adds 5
        // Without conflict resolution, this would throw ConcurrencyException.
        // With resolution: loads their 3 Incremented events, merges state to count=3,
        // projects our 5 Incremented events → count=8 (within bounds), returns ourEvents.
        var result = await agg2.Handle(new CounterCommand.Add(5));

        Assert.True(result.IsOk);
        Assert.Equal(8, result.Value.Count);
        Assert.Equal(8, agg2.Version);

        // Store should have all 8 events
        var stream = _store.GetStream(_streamId);
        Assert.Equal(8, stream.Count);
    }

    [Fact]
    public async Task Handle_CommutativeConflict_WithDecrements_ResolvesAutomatically()
    {
        // Seed with 50
        var seed = CreateAggregate();
        await seed.Handle(new CounterCommand.Add(50));
        seed.Dispose();
        _aggregate = null;

        // Two aggregates load from version 50
        var agg1 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        using var agg2 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        _aggregate = agg1;

        // agg1 subtracts 10 → count=40
        await agg1.Handle(new CounterCommand.Add(-10));

        // agg2 subtracts 20 → conflict, resolves to count=20 (50-10-20=20)
        var result = await agg2.Handle(new CounterCommand.Add(-20));

        Assert.True(result.IsOk);
        Assert.Equal(20, result.Value.Count);
    }

    // ── Conflict Resolution: Non-Commutative (Reset) ──

    [Fact]
    public async Task Handle_OurResetConflictsWithTheirChanges_ThrowsConcurrencyException()
    {
        // Seed with 10
        var seed = CreateAggregate();
        await seed.Handle(new CounterCommand.Add(10));
        seed.Dispose();
        _aggregate = null;

        var agg1 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        using var agg2 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        _aggregate = agg1;

        // agg1 adds 5 → count=15
        await agg1.Handle(new CounterCommand.Add(5));

        // agg2 tries to reset → conflict, Reset is non-commutative
        // Runner converts ConflictNotResolved to ConcurrencyException at boundary
        await Assert.ThrowsAsync<ConcurrencyException>(async () =>
            await agg2.Handle(new CounterCommand.Reset()));
    }

    [Fact]
    public async Task Handle_TheirResetConflictsWithOurChanges_ThrowsConcurrencyException()
    {
        // Seed with 10
        var seed = CreateAggregate();
        await seed.Handle(new CounterCommand.Add(10));
        seed.Dispose();
        _aggregate = null;

        var agg1 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        using var agg2 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        _aggregate = agg1;

        // agg1 resets → count=0
        await agg1.Handle(new CounterCommand.Reset());

        // agg2 tries to add 5 → conflict, their reset makes our add meaningless
        // Runner converts ConflictNotResolved to ConcurrencyException at boundary
        await Assert.ThrowsAsync<ConcurrencyException>(async () =>
            await agg2.Handle(new CounterCommand.Add(5)));
    }

    // ── Conflict Resolution: Invariant Violation ──

    [Fact]
    public async Task Handle_ResolvedStateExceedsMax_ThrowsConcurrencyException()
    {
        // Seed with 90
        var seed = CreateAggregate();
        await seed.Handle(new CounterCommand.Add(90));
        seed.Dispose();
        _aggregate = null;

        var agg1 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        using var agg2 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        _aggregate = agg1;

        // agg1 adds 8 → count=98
        await agg1.Handle(new CounterCommand.Add(8));

        // agg2 adds 5 → conflict, merged state=98, projected=103 > MaxCount
        await Assert.ThrowsAsync<ConcurrencyException>(async () =>
            await agg2.Handle(new CounterCommand.Add(5)));
    }

    [Fact]
    public async Task Handle_ResolvedStateBelowZero_ThrowsConcurrencyException()
    {
        // Seed with 10
        var seed = CreateAggregate();
        await seed.Handle(new CounterCommand.Add(10));
        seed.Dispose();
        _aggregate = null;

        var agg1 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        using var agg2 = await ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Load(_store, _streamId, default);
        _aggregate = agg1;

        // agg1 subtracts 8 → count=2
        await agg1.Handle(new CounterCommand.Add(-8));

        // agg2 subtracts 5 → conflict, merged state=2, projected=-3 < 0
        await Assert.ThrowsAsync<ConcurrencyException>(async () =>
            await agg2.Handle(new CounterCommand.Add(-5)));
    }

    // ── Load / Replay ──

    [Fact]
    public async Task Load_ReplaysEventsToRebuildState()
    {
        var original = CreateAggregate();
        await original.Handle(new CounterCommand.Add(5));
        await original.Handle(new CounterCommand.Add(-2));
        original.Dispose();
        _aggregate = null;

        var reloaded = await LoadAggregate();

        Assert.Equal(3, reloaded.State.Count);
        Assert.Equal(7, reloaded.Version);
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

        Assert.Equal(5, aggregate.State.Count);

        var state = await aggregate.Rebuild();

        Assert.Equal(7, state.Count);
        Assert.Equal(7, aggregate.Version);
    }

    // ── Idempotent Command ──

    [Fact]
    public async Task Handle_IdempotentCommand_ReturnsOkWithNoEvents()
    {
        var aggregate = CreateAggregate();

        var result = await aggregate.Handle(new CounterCommand.Add(0));

        Assert.True(result.IsOk);
        Assert.Equal(0, aggregate.Version);
    }

    // ── Thread Safety ──

    [Fact]
    public async Task Handle_ConcurrentCalls_SerializesCorrectly()
    {
        var aggregate = CreateAggregate();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => aggregate.Handle(new CounterCommand.Add(1)).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);

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

    // ── State After Resolved Conflict ──

    [Fact]
    public async Task Handle_AfterResolvedConflict_StateAndVersionAreCorrect()
    {
        var agg1 = CreateAggregate();
        using var agg2 = ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Create(_store, _streamId, default);

        // agg1 adds 2 → version 2
        await agg1.Handle(new CounterCommand.Add(2));

        // agg2 adds 3 → conflict resolved → version 5 (2 theirs + 3 ours)
        var result = await agg2.Handle(new CounterCommand.Add(3));

        Assert.True(result.IsOk);
        Assert.Equal(5, result.Value.Count);
        Assert.Equal(5, agg2.Version);

        // agg2 can continue handling commands at the new version
        var result2 = await agg2.Handle(new CounterCommand.Add(1));

        Assert.True(result2.IsOk);
        Assert.Equal(6, result2.Value.Count);
        Assert.Equal(6, agg2.Version);
    }

    // ── Effects After Resolved Conflict ──

    [Fact]
    public async Task Handle_AfterResolvedConflict_EffectsFromResolvedEventsAreRecorded()
    {
        var agg1 = CreateAggregate();
        using var agg2 = ResolvingAggregateRunner<CounterDecider, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError, Unit>.Create(_store, _streamId, default);

        await agg1.Handle(new CounterCommand.Add(2));
        await agg2.Handle(new CounterCommand.Add(3));

        // agg2 should have 3 effects (from the 3 resolved Incremented events)
        Assert.Equal(3, agg2.Effects.Count);
    }
}
