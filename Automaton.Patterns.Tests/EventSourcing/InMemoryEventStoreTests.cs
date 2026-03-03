// =============================================================================
// InMemoryEventStore Tests
// =============================================================================

using Automaton.Patterns.EventSourcing;
using Automaton.Patterns.Tests.TestDomain;

namespace Automaton.Patterns.Tests.EventSourcing;

public class InMemoryEventStoreTests
{
    private readonly InMemoryEventStore<CounterEvent> _store = new();
    private const string _streamId = "counter-1";

    // ── Append ──

    [Fact]
    public async Task AppendAsync_NewStream_CreatesStreamWithEvents()
    {
        CounterEvent[] events = [new CounterEvent.Incremented(), new CounterEvent.Incremented()];

        var stored = await _store.AppendAsync(_streamId, events, 0);

        Assert.Equal(2, stored.Count);
        Assert.Equal(1, stored[0].SequenceNumber);
        Assert.Equal(2, stored[1].SequenceNumber);
        Assert.IsType<CounterEvent.Incremented>(stored[0].Event);
    }

    [Fact]
    public async Task AppendAsync_ExistingStream_AppendsToEnd()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented()], 0);

        var stored = await _store.AppendAsync(_streamId,
            [new CounterEvent.Decremented()], 1);

        var single = Assert.Single(stored);
        Assert.Equal(2, single.SequenceNumber);
        Assert.IsType<CounterEvent.Decremented>(single.Event);
    }

    [Fact]
    public async Task AppendAsync_WrongVersion_ThrowsConcurrencyException()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented()], 0);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            _store.AppendAsync(_streamId,
                [new CounterEvent.Incremented()], 0).AsTask());

        Assert.Equal(_streamId, ex.StreamId);
        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task AppendAsync_EmptyEvents_Succeeds()
    {
        var stored = await _store.AppendAsync(_streamId, [], 0);
        Assert.Empty(stored);
    }

    [Fact]
    public async Task AppendAsync_AssignsTimestamp()
    {
        var before = DateTimeOffset.UtcNow;

        var stored = await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented()], 0);

        var after = DateTimeOffset.UtcNow;
        Assert.InRange(stored[0].Timestamp, before, after);
    }

    // ── Load ──

    [Fact]
    public async Task LoadAsync_EmptyStream_ReturnsEmpty()
    {
        var events = await _store.LoadAsync("nonexistent");
        Assert.Empty(events);
    }

    [Fact]
    public async Task LoadAsync_ReturnsAllEventsInOrder()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented(), new CounterEvent.Incremented()], 0);
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Decremented()], 2);

        var events = await _store.LoadAsync(_streamId);

        Assert.Equal(3, events.Count);
        Assert.Equal(1, events[0].SequenceNumber);
        Assert.Equal(2, events[1].SequenceNumber);
        Assert.Equal(3, events[2].SequenceNumber);
        Assert.IsType<CounterEvent.Incremented>(events[0].Event);
        Assert.IsType<CounterEvent.Decremented>(events[2].Event);
    }

    // ── Load After Version ──

    [Fact]
    public async Task LoadAsync_AfterVersion_ReturnsOnlyNewerEvents()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented(), new CounterEvent.Incremented()], 0);
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Decremented()], 2);

        var events = await _store.LoadAsync(_streamId, afterVersion: 1);

        Assert.Equal(2, events.Count);
        Assert.Equal(2, events[0].SequenceNumber);
        Assert.Equal(3, events[1].SequenceNumber);
    }

    [Fact]
    public async Task LoadAsync_AfterVersion_NonexistentStream_ReturnsEmpty()
    {
        var events = await _store.LoadAsync("nonexistent", afterVersion: 5);
        Assert.Empty(events);
    }

    [Fact]
    public async Task LoadAsync_AfterVersion_BeyondEnd_ReturnsEmpty()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented()], 0);

        var events = await _store.LoadAsync(_streamId, afterVersion: 10);
        Assert.Empty(events);
    }

    // ── GetStream (test helper) ──

    [Fact]
    public async Task GetStream_ReturnsEventsForAssertions()
    {
        await _store.AppendAsync(_streamId,
            [new CounterEvent.Incremented()], 0);

        var stream = _store.GetStream(_streamId);
        var single = Assert.Single(stream);
        Assert.IsType<CounterEvent.Incremented>(single.Event);
    }

    [Fact]
    public void GetStream_NonexistentStream_ReturnsEmpty()
    {
        var stream = _store.GetStream("nonexistent");
        Assert.Empty(stream);
    }

    // ── Stream isolation ──

    [Fact]
    public async Task Streams_AreIsolated()
    {
        await _store.AppendAsync("stream-a",
            [new CounterEvent.Incremented()], 0);
        await _store.AppendAsync("stream-b",
            [new CounterEvent.Decremented(), new CounterEvent.Decremented()], 0);

        var streamA = await _store.LoadAsync("stream-a");
        var streamB = await _store.LoadAsync("stream-b");

        Assert.Single(streamA);
        Assert.Equal(2, streamB.Count);
    }

    // ── Cancellation ──

    [Fact]
    public async Task AppendAsync_CancelledToken_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _store.AppendAsync(_streamId,
                [new CounterEvent.Incremented()], 0, cts.Token).AsTask());
    }

    [Fact]
    public async Task LoadAsync_CancelledToken_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _store.LoadAsync(_streamId, cts.Token).AsTask());
    }
}
