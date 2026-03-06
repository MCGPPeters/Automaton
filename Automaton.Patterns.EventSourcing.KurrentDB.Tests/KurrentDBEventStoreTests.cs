// =============================================================================
// KurrentDBEventStore Integration Tests
// =============================================================================
// These tests require a running KurrentDB instance. By default they connect to
// localhost:2113. Mark with [Trait("Category", "Integration")] so they can be
// excluded in CI without a database.
//
// Start KurrentDB locally:
//     docker run --rm -d -p 2113:2113 \
//       -e KURRENTDB_INSECURE=true \
//       -e KURRENTDB_ENABLE_ATOM_PUB_OVER_HTTP=true \
//       docker.kurrent.io/kurrent-latest/kurrentdb:latest
// =============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using KurrentDB.Client;

namespace Automaton.Patterns.EventSourcing.KurrentDB.Tests;

// ── Test Event Types ──

[JsonDerivedType(typeof(Incremented), "Incremented")]
[JsonDerivedType(typeof(Decremented), "Decremented")]
[JsonDerivedType(typeof(WasReset), "WasReset")]
public interface TestEvent
{
    record Incremented : TestEvent;
    record Decremented : TestEvent;
    record WasReset : TestEvent;
}

// ── Serialization Helpers ──

file static class Serialization
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static (string EventType, ReadOnlyMemory<byte> Data) Serialize(TestEvent e)
    {
        var eventType = e switch
        {
            TestEvent.Incremented => nameof(TestEvent.Incremented),
            TestEvent.Decremented => nameof(TestEvent.Decremented),
            TestEvent.WasReset => nameof(TestEvent.WasReset),
            _ => throw new ArgumentOutOfRangeException(nameof(e))
        };

        var data = JsonSerializer.SerializeToUtf8Bytes(e, _options);
        return (eventType, data);
    }

    public static TestEvent Deserialize(string eventType, ReadOnlyMemory<byte> data) =>
        eventType switch
        {
            nameof(TestEvent.Incremented) =>
                JsonSerializer.Deserialize<TestEvent.Incremented>(data.Span, _options)!,
            nameof(TestEvent.Decremented) =>
                JsonSerializer.Deserialize<TestEvent.Decremented>(data.Span, _options)!,
            nameof(TestEvent.WasReset) =>
                JsonSerializer.Deserialize<TestEvent.WasReset>(data.Span, _options)!,
            _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unknown event type")
        };
}

// ── Integration Tests ──

/// <summary>
/// Integration tests that exercise the full KurrentDB EventStore adapter.
/// Mirrors the InMemoryEventStoreTests contract to verify behavioral equivalence.
/// </summary>
/// <remarks>
/// Requires a running KurrentDB instance on localhost:2113.
/// Run with: <c>dotnet test --filter "Category=Integration"</c>
/// </remarks>
[Trait("Category", "Integration")]
public class KurrentDBEventStoreTests : IAsyncLifetime
{
    private KurrentDBClient _client = null!;
    private KurrentDBEventStore<TestEvent> _store = null!;

    /// <summary>
    /// Generates a unique stream ID per test to avoid cross-contamination.
    /// </summary>
    private static string UniqueStreamId() =>
        $"test-{Guid.NewGuid():N}";

    public Task InitializeAsync()
    {
        var settings = KurrentDBClientSettings.Create("esdb://localhost:2113?tls=false");
        _client = new KurrentDBClient(settings);
        _store = new KurrentDBEventStore<TestEvent>(
            _client,
            Serialization.Serialize,
            Serialization.Deserialize);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    // ── Append ──

    [Fact]
    public async Task Append_NewStream_CreatesStreamWithEvents()
    {
        var streamId = UniqueStreamId();
        TestEvent[] events = [new TestEvent.Incremented(), new TestEvent.Incremented()];

        var stored = await _store.AppendAsync(streamId, events, 0);

        Assert.Equal(2, stored.Count);
        Assert.Equal(1, stored[0].SequenceNumber);
        Assert.Equal(2, stored[1].SequenceNumber);
        Assert.IsType<TestEvent.Incremented>(stored[0].Event);
    }

    [Fact]
    public async Task Append_ExistingStream_AppendsToEnd()
    {
        var streamId = UniqueStreamId();
        await _store.AppendAsync(streamId, [new TestEvent.Incremented()], 0);

        var stored = await _store.AppendAsync(streamId, [new TestEvent.Decremented()], 1);

        var single = Assert.Single(stored);
        Assert.Equal(2, single.SequenceNumber);
        Assert.IsType<TestEvent.Decremented>(single.Event);
    }

    [Fact]
    public async Task Append_WrongVersion_ThrowsConcurrencyException()
    {
        var streamId = UniqueStreamId();
        await _store.AppendAsync(streamId, [new TestEvent.Incremented()], 0);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            _store.AppendAsync(streamId, [new TestEvent.Incremented()], 0).AsTask());

        Assert.Equal(streamId, ex.StreamId);
        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task Append_EmptyEvents_Succeeds()
    {
        var streamId = UniqueStreamId();
        var stored = await _store.AppendAsync(streamId, [], 0);
        Assert.Empty(stored);
    }

    // ── Load ──

    [Fact]
    public async Task Load_NonexistentStream_ReturnsEmpty()
    {
        var events = await _store.LoadAsync(UniqueStreamId());
        Assert.Empty(events);
    }

    [Fact]
    public async Task Load_ReturnsAllEventsInOrder()
    {
        var streamId = UniqueStreamId();
        await _store.AppendAsync(streamId,
            [new TestEvent.Incremented(), new TestEvent.Incremented()], 0);
        await _store.AppendAsync(streamId,
            [new TestEvent.Decremented()], 2);

        var events = await _store.LoadAsync(streamId);

        Assert.Equal(3, events.Count);
        Assert.Equal(1, events[0].SequenceNumber);
        Assert.Equal(2, events[1].SequenceNumber);
        Assert.Equal(3, events[2].SequenceNumber);
        Assert.IsType<TestEvent.Incremented>(events[0].Event);
        Assert.IsType<TestEvent.Decremented>(events[2].Event);
    }

    [Fact]
    public async Task Load_DeserializesEventsCorrectly()
    {
        var streamId = UniqueStreamId();
        TestEvent[] events =
        [
            new TestEvent.Incremented(),
            new TestEvent.Decremented(),
            new TestEvent.WasReset()
        ];

        await _store.AppendAsync(streamId, events, 0);
        var loaded = await _store.LoadAsync(streamId);

        Assert.Equal(3, loaded.Count);
        Assert.IsType<TestEvent.Incremented>(loaded[0].Event);
        Assert.IsType<TestEvent.Decremented>(loaded[1].Event);
        Assert.IsType<TestEvent.WasReset>(loaded[2].Event);
    }

    [Fact]
    public async Task Load_PreservesTimestamps()
    {
        var streamId = UniqueStreamId();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await _store.AppendAsync(streamId, [new TestEvent.Incremented()], 0);
        var loaded = await _store.LoadAsync(streamId);

        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        // KurrentDB assigns server-side timestamps
        Assert.InRange(loaded[0].Timestamp, before, after);
    }

    // ── Load After Version ──

    [Fact]
    public async Task LoadAfterVersion_ReturnsOnlyNewerEvents()
    {
        var streamId = UniqueStreamId();
        await _store.AppendAsync(streamId,
            [new TestEvent.Incremented(), new TestEvent.Incremented()], 0);
        await _store.AppendAsync(streamId,
            [new TestEvent.Decremented()], 2);

        var events = await _store.LoadAsync(streamId, afterVersion: 1);

        Assert.Equal(2, events.Count);
        Assert.Equal(2, events[0].SequenceNumber);
        Assert.Equal(3, events[1].SequenceNumber);
    }

    [Fact]
    public async Task LoadAfterVersion_NonexistentStream_ReturnsEmpty()
    {
        var events = await _store.LoadAsync(UniqueStreamId(), afterVersion: 5);
        Assert.Empty(events);
    }

    [Fact]
    public async Task LoadAfterVersion_BeyondEnd_ReturnsEmpty()
    {
        var streamId = UniqueStreamId();
        await _store.AppendAsync(streamId, [new TestEvent.Incremented()], 0);

        var events = await _store.LoadAsync(streamId, afterVersion: 10);
        Assert.Empty(events);
    }

    // ── Stream Isolation ──

    [Fact]
    public async Task Streams_AreIsolated()
    {
        var streamA = UniqueStreamId();
        var streamB = UniqueStreamId();

        await _store.AppendAsync(streamA, [new TestEvent.Incremented()], 0);
        await _store.AppendAsync(streamB,
            [new TestEvent.Decremented(), new TestEvent.Decremented()], 0);

        var eventsA = await _store.LoadAsync(streamA);
        var eventsB = await _store.LoadAsync(streamB);

        Assert.Single(eventsA);
        Assert.Equal(2, eventsB.Count);
    }

    // ── Optimistic Concurrency ──

    [Fact]
    public async Task Append_NewStream_WrongInitialVersion_ThrowsConcurrencyException()
    {
        var streamId = UniqueStreamId();

        // Expect version 5 on a nonexistent stream
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            _store.AppendAsync(streamId, [new TestEvent.Incremented()], 5).AsTask());
    }

    [Fact]
    public async Task Append_ConcurrentAppends_SecondThrowsConcurrencyException()
    {
        var streamId = UniqueStreamId();
        await _store.AppendAsync(streamId, [new TestEvent.Incremented()], 0);

        // Two concurrent appends expecting version 1
        var task1 = _store.AppendAsync(streamId, [new TestEvent.Incremented()], 1).AsTask();
        await task1;

        // Second append should fail — version is now 2, not 1
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            _store.AppendAsync(streamId, [new TestEvent.Decremented()], 1).AsTask());
    }

    // ── Roundtrip with AggregateRunner ──

    [Fact]
    public async Task Roundtrip_AppendAndLoad_Consistency()
    {
        var streamId = UniqueStreamId();

        // Append 3 events in 3 batches
        await _store.AppendAsync(streamId, [new TestEvent.Incremented()], 0);
        await _store.AppendAsync(streamId, [new TestEvent.Incremented()], 1);
        await _store.AppendAsync(streamId, [new TestEvent.WasReset()], 2);

        var all = await _store.LoadAsync(streamId);
        Assert.Equal(3, all.Count);
        Assert.Equal(1, all[0].SequenceNumber);
        Assert.Equal(2, all[1].SequenceNumber);
        Assert.Equal(3, all[2].SequenceNumber);

        // Load after version 2 — only the reset
        var tail = await _store.LoadAsync(streamId, afterVersion: 2);
        var single = Assert.Single(tail);
        Assert.IsType<TestEvent.WasReset>(single.Event);
        Assert.Equal(3, single.SequenceNumber);
    }
}

// ── Unit Tests (no KurrentDB required) ──

/// <summary>
/// Unit tests that validate constructor preconditions and the delegate-based
/// design without needing a KurrentDB server.
/// </summary>
public class KurrentDBEventStoreUnitTests
{
    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KurrentDBEventStore<TestEvent>(
                null!,
                Serialization.Serialize,
                Serialization.Deserialize));
    }

    [Fact]
    public void Constructor_NullSerialize_Throws()
    {
        var settings = KurrentDBClientSettings.Create("esdb://localhost:2113?tls=false");
        using var client = new KurrentDBClient(settings);

        Assert.Throws<ArgumentNullException>(() =>
            new KurrentDBEventStore<TestEvent>(
                client,
                null!,
                Serialization.Deserialize));
    }

    [Fact]
    public void Constructor_NullDeserialize_Throws()
    {
        var settings = KurrentDBClientSettings.Create("esdb://localhost:2113?tls=false");
        using var client = new KurrentDBClient(settings);

        Assert.Throws<ArgumentNullException>(() =>
            new KurrentDBEventStore<TestEvent>(
                client,
                Serialization.Serialize,
                null!));
    }
}
