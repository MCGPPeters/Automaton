// =============================================================================
// Projection — Read Model Builder from Event Streams
// =============================================================================
// Projections are folds over event streams that produce read-optimized views.
// They consume events from an EventStore and maintain a running read model.
//
// Projections can:
// - Build initial read models by replaying all events
// - Catch up from a known position (incremental projection)
// - Apply individual events for live projections
// =============================================================================

using System.Diagnostics;

namespace Automaton.Patterns.EventSourcing;

/// <summary>
/// Builds a read model by folding over an event stream.
/// </summary>
/// <remarks>
/// <para>
/// A projection is a left fold with a different accumulator than the aggregate state.
/// While the aggregate maintains the write model (for command validation), projections
/// build read-optimized views of the same event stream.
/// </para>
/// <para>
/// Common use cases:
/// <list type="bullet">
///     <item><description>Dashboard summaries (total counts, aggregations)</description></item>
///     <item><description>Denormalized views for query APIs</description></item>
///     <item><description>Audit logs and history trails</description></item>
///     <item><description>Search indexes</description></item>
/// </list>
/// </para>
/// <example>
/// <code>
/// var projection = new Projection&lt;MyEvent, DashboardStats&gt;(
///     initial: new DashboardStats(0, 0),
///     apply: (stats, @event) =&gt; @event switch
///     {
///         MyEvent.OrderPlaced =&gt; stats with { TotalOrders = stats.TotalOrders + 1 },
///         MyEvent.OrderCancelled =&gt; stats with { Cancellations = stats.Cancellations + 1 },
///         _ =&gt; stats
///     });
///
/// var store = new InMemoryEventStore&lt;MyEvent&gt;();
/// await projection.Project(store, "orders-1");
/// // projection.ReadModel == DashboardStats { TotalOrders = 5, Cancellations = 1 }
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TEvent">The domain event type.</typeparam>
/// <typeparam name="TReadModel">The read model type.</typeparam>
public sealed class Projection<TEvent, TReadModel>
{
    private readonly Func<TReadModel, TEvent, TReadModel> _apply;
    private long _lastProcessedVersion;

    /// <summary>
    /// Creates a projection with an initial read model and an apply function.
    /// </summary>
    /// <param name="initial">The initial read model state.</param>
    /// <param name="apply">
    /// The fold function that applies an event to the read model.
    /// Must be pure: return a new read model from the current model and event.
    /// </param>
    public Projection(TReadModel initial, Func<TReadModel, TEvent, TReadModel> apply)
    {
        ReadModel = initial;
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    /// <summary>
    /// The current read model.
    /// </summary>
    public TReadModel ReadModel { get; private set; }

    /// <summary>
    /// The last processed event version (sequence number).
    /// Used for incremental catch-up projections.
    /// </summary>
    public long LastProcessedVersion => _lastProcessedVersion;

    /// <summary>
    /// Projects all events from a stream into the read model.
    /// </summary>
    /// <remarks>
    /// Loads the entire event stream and folds all events. For incremental
    /// projection from a known position, use <see cref="CatchUp"/>.
    /// </remarks>
    /// <param name="store">The event store to read from.</param>
    /// <param name="streamId">The stream to project.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The projected read model.</returns>
    public async ValueTask<TReadModel> Project(
        EventStore<TEvent> store,
        string streamId,
        CancellationToken cancellationToken = default)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("Projection.Project");
        activity?.SetTag("es.stream.id", streamId);
        activity?.SetTag("es.projection.type", typeof(TReadModel).Name);

        var events = await store.LoadAsync(streamId, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < events.Count; i++)
        {
            ReadModel = _apply(ReadModel, events[i].Event);
        }

        _lastProcessedVersion = events.Count > 0
            ? events[events.Count - 1].SequenceNumber
            : _lastProcessedVersion;

        activity?.SetTag("es.event.count", events.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return ReadModel;
    }

    /// <summary>
    /// Catches up the projection by processing only events after the last processed version.
    /// </summary>
    /// <remarks>
    /// Efficient for live projections that need to stay current without replaying
    /// the entire stream. The projection remembers its last processed version
    /// and only loads new events.
    /// </remarks>
    /// <param name="store">The event store to read from.</param>
    /// <param name="streamId">The stream to catch up.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The updated read model.</returns>
    public async ValueTask<TReadModel> CatchUp(
        EventStore<TEvent> store,
        string streamId,
        CancellationToken cancellationToken = default)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("Projection.CatchUp");
        activity?.SetTag("es.stream.id", streamId);
        activity?.SetTag("es.projection.type", typeof(TReadModel).Name);
        activity?.SetTag("es.after_version", _lastProcessedVersion);

        var events = await store.LoadAsync(streamId, _lastProcessedVersion, cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < events.Count; i++)
        {
            ReadModel = _apply(ReadModel, events[i].Event);
        }

        _lastProcessedVersion = events.Count > 0
            ? events[events.Count - 1].SequenceNumber
            : _lastProcessedVersion;

        activity?.SetTag("es.event.count", events.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return ReadModel;
    }

    /// <summary>
    /// Applies a single event to the read model.
    /// </summary>
    /// <remarks>
    /// Used for live projections that process events as they arrive
    /// (e.g., via a subscription or observer).
    /// </remarks>
    /// <param name="event">The event to apply.</param>
    /// <returns>The updated read model.</returns>
    public TReadModel Apply(TEvent @event)
    {
        ReadModel = _apply(ReadModel, @event);
        return ReadModel;
    }

    /// <summary>
    /// Applies a stored event to the read model, updating the last processed version.
    /// </summary>
    /// <param name="storedEvent">The stored event to apply.</param>
    /// <returns>The updated read model.</returns>
    public TReadModel Apply(StoredEvent<TEvent> storedEvent)
    {
        ReadModel = _apply(ReadModel, storedEvent.Event);
        _lastProcessedVersion = storedEvent.SequenceNumber;
        return ReadModel;
    }
}
