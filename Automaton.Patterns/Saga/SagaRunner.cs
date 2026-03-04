// =============================================================================
// SagaRunner — Event-Sourced Saga (Process Manager) Runtime
// =============================================================================
// Runs a Saga as an event-sourced process manager:
//
//     DomainEvent → Transition(state) → (State', Effect)
//         → Persist the domain event to the saga's own stream
//         → Return the effect for the caller to dispatch
//
// The saga's state is rebuilt by replaying its event stream — the exact
// same pattern as aggregate event sourcing, but the "commands" (effects)
// flow outward to other aggregates instead of being stored.
//
// Thread safety, tracing, and optimistic concurrency follow the same
// patterns as AggregateRunner.
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Automaton.Patterns.EventSourcing;

namespace Automaton.Patterns.Saga;

/// <summary>
/// OpenTelemetry-compatible tracing for Saga components.
/// </summary>
public static class SagaDiagnostics
{
    /// <summary>
    /// The ActivitySource name for Saga operations.
    /// </summary>
    public const string SourceName = "Automaton.Patterns.Saga";

    /// <summary>
    /// The shared ActivitySource for all Saga tracing.
    /// </summary>
    internal static ActivitySource Source { get; } = new(
        SourceName,
        typeof(SagaDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}

/// <summary>
/// Runs a <see cref="Saga{TState,TEvent,TEffect,TParameters}"/> as an event-sourced process manager.
/// </summary>
/// <remarks>
/// <para>
/// The saga runner receives domain events, transitions the saga state, persists
/// the event to the saga's own stream (for durability), and returns the produced
/// effect for the caller to dispatch.
/// </para>
/// <para>
/// Unlike an aggregate (which validates commands before producing events), a saga
/// reacts to events that have already happened — there is no validation step.
/// The saga's transition function determines what should happen next.
/// </para>
/// <para>
/// <b>Durability:</b> The saga's event stream is persisted via <see cref="EventStore{TEvent}"/>,
/// so saga state survives restarts. On reload, the saga is reconstructed by replaying
/// its stream through the transition function.
/// </para>
/// <para>
/// <b>Effect dispatch:</b> The runner does NOT dispatch effects (commands). It returns
/// them to the caller, who is responsible for routing them to the appropriate aggregates.
/// This keeps the saga runtime infrastructure-agnostic.
/// </para>
/// <example>
/// <code>
/// var store = new InMemoryEventStore&lt;OrderDomainEvent&gt;();
/// var saga = SagaRunner&lt;OrderFulfillment, OrderSagaState,
///     OrderDomainEvent, FulfillmentCommand&gt;.Create(store, "saga-order-42");
///
/// var effect = await saga.Handle(new OrderDomainEvent.PaymentReceived("order-42"));
/// // effect == FulfillmentCommand.ShipOrder("order-42")
/// // Caller dispatches: await orderAggregate.Handle(effect);
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TSaga">The Saga type providing Initialize, Transition, and IsTerminal.</typeparam>
/// <typeparam name="TState">The saga's progress state.</typeparam>
/// <typeparam name="TEvent">Domain events the saga reacts to.</typeparam>
/// <typeparam name="TEffect">Effects (commands) the saga produces.</typeparam>
/// <typeparam name="TParameters">The type of parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Initialize"/>.</typeparam>
public sealed class SagaRunner<TSaga, TState, TEvent, TEffect, TParameters> : IDisposable
    where TSaga : Saga<TState, TEvent, TEffect, TParameters>
{
    private static readonly string _sagaTypeName = typeof(TSaga).Name;

    private readonly EventStore<TEvent> _store;
    private readonly string _streamId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _threadSafe;
    private readonly List<TEffect> _effects = [];
    private readonly TParameters _parameters;
    private TState _state;
    private long _version;

    /// <summary>
    /// The current state of the saga.
    /// </summary>
    public TState State => _state;

    /// <summary>
    /// The current stream version (number of events persisted to the saga's stream).
    /// </summary>
    public long Version => _version;

    /// <summary>
    /// The stream identifier for this saga instance.
    /// </summary>
    public string StreamId => _streamId;

    /// <summary>
    /// All effects produced during the saga's lifetime.
    /// </summary>
    public IReadOnlyList<TEffect> Effects => _effects;

    /// <summary>
    /// Whether the saga has reached a terminal state.
    /// </summary>
    public bool IsTerminal => TSaga.IsTerminal(_state);

    private SagaRunner(
        EventStore<TEvent> store,
        string streamId,
        TParameters parameters,
        TState state,
        long version,
        bool threadSafe)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _streamId = streamId ?? throw new ArgumentNullException(nameof(streamId));
        _parameters = parameters;
        _state = state;
        _version = version;
        _threadSafe = threadSafe;
    }

    /// <summary>
    /// Creates a new saga from its initial state.
    /// </summary>
    /// <param name="store">The event store to persist saga events to.</param>
    /// <param name="streamId">
    /// The stream identifier for this saga instance.
    /// Convention: <c>"SagaType-{correlationId}"</c> (e.g., <c>"OrderFulfillment-order-42"</c>).
    /// </param>
    /// <param name="parameters">Initialization parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Initialize"/>.</param>
    /// <param name="threadSafe">
    /// When <c>true</c> (default), Handle calls are serialized via a semaphore.
    /// </param>
    public static SagaRunner<TSaga, TState, TEvent, TEffect, TParameters> Create(
        EventStore<TEvent> store,
        string streamId,
        TParameters parameters,
        bool threadSafe = true)
    {
        using var activity = SagaDiagnostics.Source.StartActivity("Saga.Create");
        activity?.SetTag("saga.type", _sagaTypeName);
        activity?.SetTag("saga.stream.id", streamId);

        var (state, _) = TSaga.Initialize(parameters);

        activity?.SetStatus(ActivityStatusCode.Ok);
        return new SagaRunner<TSaga, TState, TEvent, TEffect, TParameters>(
            store, streamId, parameters, state, 0, threadSafe);
    }

    /// <summary>
    /// Loads a saga by replaying its event stream from the store.
    /// </summary>
    /// <param name="store">The event store to load events from.</param>
    /// <param name="streamId">The stream identifier for this saga instance.</param>
    /// <param name="parameters">Initialization parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Initialize"/>.</param>
    /// <param name="threadSafe">
    /// When <c>true</c> (default), Handle calls are serialized via a semaphore.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<SagaRunner<TSaga, TState, TEvent, TEffect, TParameters>> Load(
        EventStore<TEvent> store,
        string streamId,
        TParameters parameters,
        bool threadSafe = true,
        CancellationToken cancellationToken = default)
    {
        using var activity = SagaDiagnostics.Source.StartActivity("Saga.Load");
        activity?.SetTag("saga.type", _sagaTypeName);
        activity?.SetTag("saga.stream.id", streamId);

        var storedEvents = await store.LoadAsync(streamId, cancellationToken).ConfigureAwait(false);
        var (seed, _) = TSaga.Initialize(parameters);

        var state = seed;
        for (var i = 0; i < storedEvents.Count; i++)
        {
            (state, _) = TSaga.Transition(state, storedEvents[i].Event);
        }

        activity?.SetTag("saga.event.count", storedEvents.Count);
        activity?.SetTag("saga.version", storedEvents.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return new SagaRunner<TSaga, TState, TEvent, TEffect, TParameters>(
            store, streamId, parameters, state, storedEvents.Count, threadSafe);
    }

    /// <summary>
    /// Handles a domain event: Transition → Persist → Return effect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event is first transitioned (computing new state and effect), then
    /// persisted to the saga's own stream for durability. The effect is returned
    /// to the caller for dispatch to the appropriate aggregate(s).
    /// </para>
    /// <para>
    /// If the saga has reached a terminal state, the event is ignored and the
    /// initial effect is returned (typically a no-op).
    /// </para>
    /// </remarks>
    /// <param name="event">The domain event to handle.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The effect produced by the saga transition.</returns>
    public ValueTask<TEffect> Handle(
        TEvent @event,
        CancellationToken cancellationToken = default)
    {
        if (_threadSafe)
        {
            var waitTask = _gate.WaitAsync(cancellationToken);
            if (waitTask.IsCompletedSuccessfully)
                return HandleAfterGate(@event, cancellationToken);

            return AwaitGateThenHandle(waitTask, @event, cancellationToken);
        }

        return HandleCore(@event, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<TEffect> AwaitGateThenHandle(
        Task waitTask, TEvent @event, CancellationToken cancellationToken)
    {
        await waitTask.ConfigureAwait(false);
        try
        {
            return await HandleCore(@event, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ValueTask<TEffect> HandleAfterGate(
        TEvent @event, CancellationToken cancellationToken)
    {
        try
        {
            var coreTask = HandleCore(@event, cancellationToken);
            if (coreTask.IsCompletedSuccessfully)
            {
                _gate.Release();
                return coreTask;
            }

            return AwaitCoreThenRelease(coreTask);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<TEffect> AwaitCoreThenRelease(ValueTask<TEffect> coreTask)
    {
        try
        {
            return await coreTask.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<TEffect> HandleCore(
        TEvent @event, CancellationToken cancellationToken)
    {
        using var activity = SagaDiagnostics.Source.StartActivity("Saga.Handle");
        activity?.SetTag("saga.type", _sagaTypeName);
        activity?.SetTag("saga.stream.id", _streamId);
        activity?.SetTag("saga.event.type", @event?.GetType().Name);

        try
        {
            // Check terminal state
            if (TSaga.IsTerminal(_state))
            {
                activity?.SetTag("saga.result", "terminal");
                activity?.SetStatus(ActivityStatusCode.Ok);
                var (_, initEffect) = TSaga.Initialize(_parameters);
                return initEffect;
            }

            // 1. Transition (synchronous, pure)
            var (newState, effect) = TSaga.Transition(_state, @event);

            // 2. Persist the event to the saga's stream
            await _store.AppendAsync(_streamId, [@event], _version, cancellationToken)
                .ConfigureAwait(false);

            // 3. Commit state
            _state = newState;
            _version++;
            _effects.Add(effect);

            activity?.SetTag("saga.result", TSaga.IsTerminal(_state) ? "terminal" : "ok");
            activity?.SetTag("saga.effect.type", effect?.GetType().Name);
            activity?.SetTag("saga.new_version", _version);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return effect;
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Disposes the semaphore used for thread safety.
    /// </summary>
    public void Dispose() => _gate.Dispose();
}
