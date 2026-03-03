// =============================================================================
// StoredEvent — Event Envelope with Metadata
// =============================================================================
// Wraps a domain event with storage metadata: sequence number, timestamp,
// and stream identity. Used by event stores and projections.
// =============================================================================

namespace Automaton.Patterns.EventSourcing;

/// <summary>
/// An envelope wrapping a stored event with metadata.
/// </summary>
/// <remarks>
/// <para>
/// Each event in a stream is assigned a monotonically increasing sequence number
/// and a timestamp recording when it was persisted. The sequence number provides
/// ordering guarantees within a stream and enables optimistic concurrency control.
/// </para>
/// <para>
/// <c>StoredEvent</c> is a readonly record struct to avoid heap allocation —
/// event streams can contain millions of events, so per-event allocation matters.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The domain event type.</typeparam>
/// <param name="SequenceNumber">
/// Monotonically increasing position within the stream. Starts at 1.
/// Used for optimistic concurrency checks (expected version).
/// </param>
/// <param name="Event">The domain event payload.</param>
/// <param name="Timestamp">When the event was appended to the store.</param>
public readonly record struct StoredEvent<TEvent>(
    long SequenceNumber,
    TEvent Event,
    DateTimeOffset Timestamp);
