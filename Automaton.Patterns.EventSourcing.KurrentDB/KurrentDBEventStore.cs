// =============================================================================
// KurrentDBEventStore — KurrentDB Implementation of EventStore<TEvent>
// =============================================================================
// Durable event persistence backed by KurrentDB (formerly EventStoreDB).
// Uses delegate-based serialization — the consumer provides serialize and
// deserialize functions, keeping the store decoupled from any specific
// serialization framework (System.Text.Json, MessagePack, Protobuf, etc.).
//
// Version mapping:
//     Our StoredEvent.SequenceNumber is 1-based (first event = 1).
//     KurrentDB StreamRevision is 0-based (first event = revision 0).
//     Mapping: SequenceNumber = StreamRevision + 1
//
// Concurrency mapping:
//     Our expectedVersion=0 (new stream)  → StreamState.NoStream
//     Our expectedVersion=N (N events)    → StreamState.StreamRevision(N-1)
//     KurrentDB WrongExpectedVersionException → our ConcurrencyException
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using KurrentDB.Client;

namespace Automaton.Patterns.EventSourcing.KurrentDB;

/// <summary>
/// KurrentDB-backed implementation of <see cref="EventStore{TEvent}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Serialization is fully delegate-driven. The consumer provides two functions:
/// <list type="bullet">
///     <item><description>
///         <c>serialize</c>: Converts a domain event to a tuple of (eventType, data).
///         The <c>eventType</c> string is stored as the KurrentDB event type metadata
///         and is used during deserialization to reconstruct the correct event type.
///     </description></item>
///     <item><description>
///         <c>deserialize</c>: Reconstructs a domain event from the event type string
///         and raw byte data.
///     </description></item>
/// </list>
/// </para>
/// <para>
/// This design follows the <em>Strategy Pattern</em> (GoF) via delegates rather than
/// interfaces — maximally flexible, no framework coupling. The consumer can use
/// System.Text.Json with <c>[JsonDerivedType]</c>, MessagePack, Protobuf, or any
/// custom binary format.
/// </para>
/// <example>
/// <code>
/// var store = new KurrentDBEventStore&lt;MyEvent&gt;(
///     client,
///     serialize: e =&gt; (e.GetType().Name, JsonSerializer.SerializeToUtf8Bytes(e, options)),
///     deserialize: (type, data) =&gt; (MyEvent)JsonSerializer.Deserialize(data.Span, typeMap[type], options)!);
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TEvent">The domain event type.</typeparam>
public sealed class KurrentDBEventStore<TEvent> : EventStore<TEvent>
{
    private readonly KurrentDBClient _client;
    private readonly Func<TEvent, (string EventType, ReadOnlyMemory<byte> Data)> _serialize;
    private readonly Func<string, ReadOnlyMemory<byte>, TEvent> _deserialize;

    /// <summary>
    /// Creates a new KurrentDB-backed event store.
    /// </summary>
    /// <param name="client">The KurrentDB client instance.</param>
    /// <param name="serialize">
    /// Converts a domain event to its wire format: an event type string
    /// (used for deserialization dispatch) and the serialized byte payload.
    /// </param>
    /// <param name="deserialize">
    /// Reconstructs a domain event from the event type string and byte payload.
    /// </param>
    public KurrentDBEventStore(
        KurrentDBClient client,
        Func<TEvent, (string EventType, ReadOnlyMemory<byte> Data)> serialize,
        Func<string, ReadOnlyMemory<byte>, TEvent> deserialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
        _deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));
    }

    /// <inheritdoc/>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<IReadOnlyList<StoredEvent<TEvent>>> AppendAsync(
        string streamId,
        IReadOnlyList<TEvent> events,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("KurrentDB.Append");
        activity?.SetTag("es.stream.id", streamId);
        activity?.SetTag("es.event.count", events.Count);
        activity?.SetTag("es.expected_version", expectedVersion);

        if (events.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            return Array.Empty<StoredEvent<TEvent>>();
        }

        // Map our 1-based expectedVersion to KurrentDB's StreamState:
        //   expectedVersion=0 (new stream) → StreamState.NoStream
        //   expectedVersion=N              → StreamState.StreamRevision(N-1)  (0-based)
        var streamState = expectedVersion == 0
            ? StreamState.NoStream
            : StreamState.StreamRevision((ulong)(expectedVersion - 1));

        var eventDataList = new EventData[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var (eventType, data) = _serialize(events[i]);
            eventDataList[i] = new EventData(Uuid.NewUuid(), eventType, data);
        }

        try
        {
            await _client.AppendToStreamAsync(
                streamId,
                streamState,
                eventDataList,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (WrongExpectedVersionException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Concurrency conflict");

            // Map KurrentDB's actual version (StreamState, 0-based) to our 1-based version.
            // ActualVersion is a nullable long. When the stream doesn't exist, KurrentDB
            // reports StreamState.NoStream which maps to -1 via ToInt64().
            // KurrentDB 0-based revision N → our version N+1.
            // Null or negative (NoStream = -1) → 0 (no events).
            var actualVersion = ex.ActualVersion is >= 0
                ? ex.ActualVersion.Value + 1
                : 0;

            throw new ConcurrencyException(streamId, expectedVersion, actualVersion);
        }

        // Build StoredEvent results with 1-based sequence numbers.
        // After a successful append, the first new event's sequence number is
        // expectedVersion + 1 (since expectedVersion = number of events before append).
        var timestamp = DateTimeOffset.UtcNow;
        var stored = new StoredEvent<TEvent>[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            stored[i] = new StoredEvent<TEvent>(
                expectedVersion + i + 1,
                events[i],
                timestamp);
        }

        activity?.SetTag("es.new_version", expectedVersion + events.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return stored;
    }

    /// <inheritdoc/>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<IReadOnlyList<StoredEvent<TEvent>>> LoadAsync(
        string streamId,
        CancellationToken cancellationToken = default)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("KurrentDB.Load");
        activity?.SetTag("es.stream.id", streamId);

        var result = _client.ReadStreamAsync(
            Direction.Forwards,
            streamId,
            StreamPosition.Start,
            cancellationToken: cancellationToken);

        // StreamNotFound → return empty (matches InMemoryEventStore behavior)
        if (await result.ReadState.ConfigureAwait(false) == ReadState.StreamNotFound)
        {
            activity?.SetTag("es.event.count", 0);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return Array.Empty<StoredEvent<TEvent>>();
        }

        var stored = new List<StoredEvent<TEvent>>();
        await foreach (var resolved in result.ConfigureAwait(false))
        {
            var @event = _deserialize(
                resolved.Event.EventType,
                resolved.Event.Data);

            // Map 0-based StreamRevision to 1-based SequenceNumber
            stored.Add(new StoredEvent<TEvent>(
                (long)resolved.Event.EventNumber.ToUInt64() + 1,
                @event,
                resolved.Event.Created));
        }

        activity?.SetTag("es.event.count", stored.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return stored;
    }

    /// <inheritdoc/>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<IReadOnlyList<StoredEvent<TEvent>>> LoadAsync(
        string streamId,
        long afterVersion,
        CancellationToken cancellationToken = default)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("KurrentDB.LoadAfter");
        activity?.SetTag("es.stream.id", streamId);
        activity?.SetTag("es.after_version", afterVersion);

        // afterVersion is 1-based. The event at afterVersion has 0-based revision
        // (afterVersion - 1). We want events AFTER that, so start at revision afterVersion.
        var startRevision = StreamPosition.FromInt64(afterVersion);

        var result = _client.ReadStreamAsync(
            Direction.Forwards,
            streamId,
            startRevision,
            cancellationToken: cancellationToken);

        if (await result.ReadState.ConfigureAwait(false) == ReadState.StreamNotFound)
        {
            activity?.SetTag("es.event.count", 0);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return Array.Empty<StoredEvent<TEvent>>();
        }

        var stored = new List<StoredEvent<TEvent>>();
        await foreach (var resolved in result.ConfigureAwait(false))
        {
            var @event = _deserialize(
                resolved.Event.EventType,
                resolved.Event.Data);

            stored.Add(new StoredEvent<TEvent>(
                (long)resolved.Event.EventNumber.ToUInt64() + 1,
                @event,
                resolved.Event.Created));
        }

        activity?.SetTag("es.event.count", stored.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return stored;
    }
}
