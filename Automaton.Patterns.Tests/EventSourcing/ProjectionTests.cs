// =============================================================================
// Projection Tests
// =============================================================================

using Automaton.Patterns.EventSourcing;
using Automaton.Patterns.Tests.TestDomain;

namespace Automaton.Patterns.Tests.EventSourcing;

/// <summary>
/// Read model for testing projections — tracks total increments and decrements.
/// </summary>
public readonly record struct CounterStats(int Increments, int Decrements, int Resets);

public class ProjectionTests
{
    private readonly InMemoryEventStore<CounterEvent> _store = new();
    private const string _streamId = "counter-1";

    private Projection<CounterEvent, CounterStats> CreateProjection() =>
        new(
            initial: new CounterStats(0, 0, 0),
            apply: (stats, @event) => @event switch
            {
                CounterEvent.Incremented => stats with { Increments = stats.Increments + 1 },
                CounterEvent.Decremented => stats with { Decrements = stats.Decrements + 1 },
                CounterEvent.WasReset => stats with { Resets = stats.Resets + 1 },
                _ => stats
            });

    // ── Project (full replay) ──

    [Fact]
    public async Task Project_EmptyStream_ReturnsInitialReadModel()
    {
        var projection = CreateProjection();

        var result = await projection.Project(_store, _streamId);

        Assert.Equal(new CounterStats(0, 0, 0), result);
    }

    [Fact]
    public async Task Project_FoldsAllEvents()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented(), new CounterEvent.Incremented(), new CounterEvent.Decremented()], 0);

        var projection = CreateProjection();
        var result = await projection.Project(_store, _streamId);

        Assert.Equal(2, result.Increments);
        Assert.Equal(1, result.Decrements);
        Assert.Equal(0, result.Resets);
    }

    [Fact]
    public async Task Project_UpdatesLastProcessedVersion()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented(), new CounterEvent.Incremented()], 0);

        var projection = CreateProjection();
        await projection.Project(_store, _streamId);

        Assert.Equal(2, projection.LastProcessedVersion);
    }

    // ── CatchUp (incremental) ──

    [Fact]
    public async Task CatchUp_ProcessesOnlyNewEvents()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented(), new CounterEvent.Incremented()], 0);

        var projection = CreateProjection();
        await projection.Project(_store, _streamId);

        // Add more events
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Decremented(), new CounterEvent.WasReset()], 2);

        // Catch up
        var result = await projection.CatchUp(_store, _streamId);

        Assert.Equal(2, result.Increments);
        Assert.Equal(1, result.Decrements);
        Assert.Equal(1, result.Resets);
        Assert.Equal(4, projection.LastProcessedVersion);
    }

    [Fact]
    public async Task CatchUp_NoNewEvents_ReadModelUnchanged()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented()], 0);

        var projection = CreateProjection();
        await projection.Project(_store, _streamId);

        var before = projection.ReadModel;
        await projection.CatchUp(_store, _streamId);

        Assert.Equal(before, projection.ReadModel);
        Assert.Equal(1, projection.LastProcessedVersion);
    }

    [Fact]
    public async Task CatchUp_EmptyStream_NoChange()
    {
        var projection = CreateProjection();
        await projection.CatchUp(_store, "nonexistent");

        Assert.Equal(new CounterStats(0, 0, 0), projection.ReadModel);
        Assert.Equal(0, projection.LastProcessedVersion);
    }

    // ── Apply (single event) ──

    [Fact]
    public void Apply_SingleEvent_UpdatesReadModel()
    {
        var projection = CreateProjection();

        projection.Apply(new CounterEvent.Incremented());
        projection.Apply(new CounterEvent.Incremented());
        projection.Apply(new CounterEvent.Decremented());

        Assert.Equal(2, projection.ReadModel.Increments);
        Assert.Equal(1, projection.ReadModel.Decrements);
    }

    [Fact]
    public void Apply_StoredEvent_UpdatesVersionAndReadModel()
    {
        var projection = CreateProjection();

        projection.Apply(new StoredEvent<CounterEvent>(1, new CounterEvent.Incremented(), DateTimeOffset.UtcNow));
        projection.Apply(new StoredEvent<CounterEvent>(2, new CounterEvent.Decremented(), DateTimeOffset.UtcNow));

        Assert.Equal(1, projection.ReadModel.Increments);
        Assert.Equal(1, projection.ReadModel.Decrements);
        Assert.Equal(2, projection.LastProcessedVersion);
    }

    // ── ReadModel property ──

    [Fact]
    public void ReadModel_ReflectsCurrentState()
    {
        var projection = CreateProjection();

        Assert.Equal(new CounterStats(0, 0, 0), projection.ReadModel);

        projection.Apply(new CounterEvent.WasReset());

        Assert.Equal(1, projection.ReadModel.Resets);
    }

    // ── Multiple streams ──

    [Fact]
    public async Task Project_DifferentStreams_IndependentProjections()
    {
        await _store.AppendAsync("counter-a",
            [new CounterEvent.Incremented(), new CounterEvent.Incremented()], 0);
        await _store.AppendAsync("counter-b",
            [new CounterEvent.Decremented()], 0);

        var projectionA = CreateProjection();
        var projectionB = CreateProjection();

        await projectionA.Project(_store, "counter-a");
        await projectionB.Project(_store, "counter-b");

        Assert.Equal(2, projectionA.ReadModel.Increments);
        Assert.Equal(0, projectionA.ReadModel.Decrements);

        Assert.Equal(0, projectionB.ReadModel.Increments);
        Assert.Equal(1, projectionB.ReadModel.Decrements);
    }
}
