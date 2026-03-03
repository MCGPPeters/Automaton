// =============================================================================
// EventStore — Async Event Persistence Abstraction
// =============================================================================
// Defines the contract for event persistence backends. Implementations can
// target in-memory stores (testing), EventStoreDB, Marten/PostgreSQL,
// CosmosDB, or any other event storage system.
//
// The interface is intentionally minimal: append events and load events.
// Optimistic concurrency is enforced via expected version on append.
// =============================================================================

namespace Automaton.Patterns.EventSourcing;

/// <summary>
/// Async abstraction for event persistence.
/// </summary>
/// <remarks>
/// <para>
/// Event stores are append-only logs partitioned by stream. Each stream
/// represents one aggregate instance and is identified by a string key.
/// </para>
/// <para>
/// All operations return <see cref="ValueTask"/> / <see cref="ValueTask{TResult}"/>
/// to avoid heap allocation when the implementation completes synchronously
/// (e.g., in-memory stores).
/// </para>
/// <para>
/// Implementations MUST enforce optimistic concurrency: if the current stream
/// version does not match <c>expectedVersion</c> on append, throw
/// <see cref="ConcurrencyException"/>.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The domain event type.</typeparam>
public interface EventStore<TEvent>
{
    /// <summary>
    /// Appends events to a stream with optimistic concurrency control.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Events are appended atomically: either all events are persisted or none are.
    /// </para>
    /// <para>
    /// <paramref name="expectedVersion"/> must equal the current stream version
    /// (number of events already in the stream). Use <c>0</c> for a new stream.
    /// If the versions don't match, a <see cref="ConcurrencyException"/> is thrown.
    /// </para>
    /// </remarks>
    /// <param name="streamId">The stream identifier (typically aggregate type + id).</param>
    /// <param name="events">The events to append.</param>
    /// <param name="expectedVersion">The expected current version of the stream.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The stored events with assigned sequence numbers and timestamps.</returns>
    /// <exception cref="ConcurrencyException">
    /// Thrown when <paramref name="expectedVersion"/> does not match the current stream version.
    /// </exception>
    ValueTask<IReadOnlyList<StoredEvent<TEvent>>> AppendAsync(
        string streamId,
        IReadOnlyList<TEvent> events,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads all events from a stream in order.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>All stored events in the stream, ordered by sequence number.</returns>
    ValueTask<IReadOnlyList<StoredEvent<TEvent>>> LoadAsync(
        string streamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads events from a stream starting after a given version.
    /// </summary>
    /// <remarks>
    /// Useful for catching up a projection or saga from a known position
    /// without replaying the entire stream.
    /// </remarks>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="afterVersion">Load events with sequence numbers greater than this value.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Stored events after the specified version, ordered by sequence number.</returns>
    ValueTask<IReadOnlyList<StoredEvent<TEvent>>> LoadAsync(
        string streamId,
        long afterVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when an optimistic concurrency conflict is detected during event append.
/// </summary>
/// <remarks>
/// This occurs when the expected stream version does not match the actual version,
/// indicating that another process has appended events since the aggregate was loaded.
/// The caller should reload the aggregate and retry the command.
/// </remarks>
public sealed class ConcurrencyException : Exception
{
    /// <summary>
    /// The stream that experienced the conflict.
    /// </summary>
    public string StreamId { get; }

    /// <summary>
    /// The version the caller expected.
    /// </summary>
    public long ExpectedVersion { get; }

    /// <summary>
    /// The actual current version of the stream.
    /// </summary>
    public long ActualVersion { get; }

    /// <summary>
    /// Creates a new concurrency exception.
    /// </summary>
    public ConcurrencyException(string streamId, long expectedVersion, long actualVersion)
        : base($"Concurrency conflict on stream '{streamId}': expected version {expectedVersion}, actual version {actualVersion}.")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
