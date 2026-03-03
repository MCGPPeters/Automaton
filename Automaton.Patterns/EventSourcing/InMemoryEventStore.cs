// =============================================================================
// InMemoryEventStore — In-Memory Event Store Implementation
// =============================================================================
// A thread-safe, in-memory implementation of EventStore for testing and
// prototyping. Not suitable for production use — events are lost on restart.
//
// Thread safety: All operations are synchronized via a lock. This is
// appropriate for testing; production stores use database-level concurrency.
// =============================================================================

using System.Diagnostics;

namespace Automaton.Patterns.EventSourcing;

/// <summary>
/// Thread-safe in-memory event store for testing and prototyping.
/// </summary>
/// <remarks>
/// <para>
/// Stores events in memory partitioned by stream. Each stream maintains
/// its own sequence numbering starting at 1. All operations are synchronized
/// via a lock to support concurrent access in tests.
/// </para>
/// <para>
/// This implementation enforces optimistic concurrency: <see cref="AppendAsync"/>
/// will throw <see cref="ConcurrencyException"/> if the expected version does not
/// match the actual stream version.
/// </para>
/// <para>
/// For production use, implement <see cref="EventStore{TEvent}"/> against a
/// durable store such as EventStoreDB, Marten/PostgreSQL, or CosmosDB.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The domain event type.</typeparam>
public sealed class InMemoryEventStore<TEvent> : EventStore<TEvent>
{
    private static readonly string _eventTypeName = typeof(TEvent).Name;

    private readonly Dictionary<string, List<StoredEvent<TEvent>>> _streams = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// All streams currently in the store.
    /// </summary>
    public IReadOnlyDictionary<string, List<StoredEvent<TEvent>>> Streams
    {
        get
        {
            lock (_lock)
            { return new Dictionary<string, List<StoredEvent<TEvent>>>(_streams); }
        }
    }

    /// <summary>
    /// Gets the events for a specific stream (for test assertions).
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <returns>The stored events, or an empty list if the stream doesn't exist.</returns>
    public IReadOnlyList<StoredEvent<TEvent>> GetStream(string streamId)
    {
        lock (_lock)
        {
            return _streams.TryGetValue(streamId, out var stream)
                ? stream.ToList()
                : [];
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<StoredEvent<TEvent>>> AppendAsync(
        string streamId,
        IReadOnlyList<TEvent> events,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = EventSourcingDiagnostics.Source.StartActivity("EventStore.Append");
        activity?.SetTag("es.stream.id", streamId);
        activity?.SetTag("es.event.type", _eventTypeName);
        activity?.SetTag("es.event.count", events.Count);
        activity?.SetTag("es.expected_version", expectedVersion);

        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                stream = [];
                _streams[streamId] = stream;
            }

            var actualVersion = stream.Count;
            if (actualVersion != expectedVersion)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Concurrency conflict");
                throw new ConcurrencyException(streamId, expectedVersion, actualVersion);
            }

            var stored = new List<StoredEvent<TEvent>>(events.Count);
            var timestamp = DateTimeOffset.UtcNow;

            for (var i = 0; i < events.Count; i++)
            {
                var storedEvent = new StoredEvent<TEvent>(
                    actualVersion + i + 1,
                    events[i],
                    timestamp);
                stream.Add(storedEvent);
                stored.Add(storedEvent);
            }

            activity?.SetTag("es.new_version", stream.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return new ValueTask<IReadOnlyList<StoredEvent<TEvent>>>(stored);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<StoredEvent<TEvent>>> LoadAsync(
        string streamId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = EventSourcingDiagnostics.Source.StartActivity("EventStore.Load");
        activity?.SetTag("es.stream.id", streamId);

        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                activity?.SetTag("es.event.count", 0);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new ValueTask<IReadOnlyList<StoredEvent<TEvent>>>(
                    Array.Empty<StoredEvent<TEvent>>());
            }

            activity?.SetTag("es.event.count", stream.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return new ValueTask<IReadOnlyList<StoredEvent<TEvent>>>(stream.ToList());
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<StoredEvent<TEvent>>> LoadAsync(
        string streamId,
        long afterVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = EventSourcingDiagnostics.Source.StartActivity("EventStore.LoadAfter");
        activity?.SetTag("es.stream.id", streamId);
        activity?.SetTag("es.after_version", afterVersion);

        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                activity?.SetTag("es.event.count", 0);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new ValueTask<IReadOnlyList<StoredEvent<TEvent>>>(
                    Array.Empty<StoredEvent<TEvent>>());
            }

            // Sequence numbers are 1-based and strictly monotonic, mapping directly
            // to list indices: SequenceNumber N lives at index N-1. GetRange uses a
            // single Array.Copy — no LINQ enumerator, no per-element predicate evaluation.
            var startIndex = (int)afterVersion;
            if (startIndex >= stream.Count)
            {
                activity?.SetTag("es.event.count", 0);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new ValueTask<IReadOnlyList<StoredEvent<TEvent>>>(
                    Array.Empty<StoredEvent<TEvent>>());
            }

            var result = stream.GetRange(startIndex, stream.Count - startIndex);

            activity?.SetTag("es.event.count", result.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return new ValueTask<IReadOnlyList<StoredEvent<TEvent>>>(result);
        }
    }
}
