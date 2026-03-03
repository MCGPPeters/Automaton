// =============================================================================
// ResolvingAggregateRunner — Event-Sourced Aggregate with Conflict Resolution
// =============================================================================
// Extends the AggregateRunner pattern with automatic concurrency conflict
// resolution via the ConflictResolver interface. Uses compile-time generic
// constraints (no reflection) to dispatch to ResolveConflicts.
//
// On ConcurrencyException:
//     1. Load "their" events committed since our known version
//     2. Replay "their" events through Transition to get merged state
//     3. Project our events on merged state to get projected state
//     4. Call ResolveConflicts(mergedState, projectedState, ourEvents, theirEvents)
//     5. Match on Result: Ok → transition and retry append, Err → propagate as ConcurrencyException
//     6. Retry up to MaxRetries times
//
// ResolveConflicts returns Result<TEvent[], ConflictNotResolved> — a pure total
// function with no exceptions for expected domain outcomes. When the domain
// signals an irreconcilable conflict via ConflictNotResolved, the runner
// converts it to a ConcurrencyException at the infrastructure boundary.
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Automaton.Patterns.EventSourcing;

/// <summary>
/// Runs a <see cref="ConflictResolver{TState,TCommand,TEvent,TEffect,TError,TParameters}"/> as an
/// event-sourced aggregate with automatic concurrency conflict resolution.
/// </summary>
/// <remarks>
/// <para>
/// This runner extends the decide-then-append pattern with a retry loop
/// for optimistic concurrency conflicts. When a <see cref="ConcurrencyException"/>
/// occurs, the runner:
/// </para>
/// <para>
/// <list type="number">
///     <item><description>Loads events committed by other processes since our last known version</description></item>
///     <item><description>Replays those events to compute the current merged state</description></item>
///     <item><description>Calls <see cref="ConflictResolver{TState,TCommand,TEvent,TEffect,TError,TParameters}.ResolveConflicts"/>
///         to reconcile our intended events with theirs</description></item>
///     <item><description>Transitions the resolved events and attempts to append again</description></item>
/// </list>
/// </para>
/// <para>
/// <b>No reflection:</b> The <typeparamref name="TDecider"/> constraint
/// <c>where TDecider : ConflictResolver&lt;...&gt;</c> provides compile-time
/// dispatch to <c>ResolveConflicts</c> via static interface methods.
/// </para>
/// <para>
/// <b>Retry limit:</b> Retries are bounded by <see cref="MaxRetries"/> (default: 3).
/// If all retries are exhausted (another process keeps winning the race),
/// a <see cref="ConcurrencyException"/> is thrown. If <c>ResolveConflicts</c>
/// returns <c>Err(ConflictNotResolved)</c>, the runner also throws
/// <see cref="ConcurrencyException"/> — irreconcilable conflicts are surfaced
/// as infrastructure failures at the boundary.
/// </para>
/// <example>
/// <code>
/// var store = new InMemoryEventStore&lt;MyEvent&gt;();
/// var aggregate = ResolvingAggregateRunner&lt;MyDecider, MyState, MyCommand,
///     MyEvent, MyEffect, MyError&gt;.Create(store, "my-aggregate-1");
///
/// // Conflicts are automatically resolved if MyDecider implements ConflictResolver
/// var result = await aggregate.Handle(new MyCommand.DoSomething());
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TDecider">The Decider type that also resolves conflicts.</typeparam>
/// <typeparam name="TState">The aggregate state.</typeparam>
/// <typeparam name="TCommand">Commands representing user intent.</typeparam>
/// <typeparam name="TEvent">Events representing validated facts.</typeparam>
/// <typeparam name="TEffect">Effects produced by transitions.</typeparam>
/// <typeparam name="TError">Errors produced by invalid commands.</typeparam>
/// <typeparam name="TParameters">The type of parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Init"/>.</typeparam>
public sealed class ResolvingAggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters> : IDisposable
    where TDecider : ConflictResolver<TState, TCommand, TEvent, TEffect, TError, TParameters>
{
    /// <summary>
    /// Maximum number of conflict resolution retries before giving up.
    /// </summary>
    public const int MaxRetries = 3;

    private static readonly string _deciderTypeName = typeof(TDecider).Name;

    private readonly EventStore<TEvent> _store;
    private readonly string _streamId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _threadSafe;
    private readonly List<TEffect> _effects = [];
    private readonly TParameters _parameters;
    private TState _state;
    private long _version;

    /// <summary>
    /// The current state of the aggregate.
    /// </summary>
    public TState State => _state;

    /// <summary>
    /// The current stream version (number of events persisted).
    /// </summary>
    public long Version => _version;

    /// <summary>
    /// The stream identifier for this aggregate.
    /// </summary>
    public string StreamId => _streamId;

    /// <summary>
    /// All effects produced during the aggregate's lifetime.
    /// </summary>
    public IReadOnlyList<TEffect> Effects => _effects;

    /// <summary>
    /// Whether the aggregate has reached a terminal state.
    /// </summary>
    public bool IsTerminal => TDecider.IsTerminal(_state);

    private ResolvingAggregateRunner(
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
    /// Creates a new aggregate from its initial state.
    /// </summary>
    /// <param name="store">The event store to persist events to.</param>
    /// <param name="streamId">
    /// The stream identifier for this aggregate instance.
    /// Convention: <c>"AggregateType-{id}"</c> (e.g., <c>"Order-42"</c>).
    /// </param>
    /// <param name="parameters">Initialization parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Init"/>.</param>
    /// <param name="threadSafe">
    /// When <c>true</c> (default), Handle calls are serialized via a semaphore.
    /// Set to <c>false</c> for single-threaded scenarios.
    /// </param>
    public static ResolvingAggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters> Create(
        EventStore<TEvent> store,
        string streamId,
        TParameters parameters,
        bool threadSafe = true)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("ResolvingAggregate.Create");
        activity?.SetTag("es.aggregate.type", _deciderTypeName);
        activity?.SetTag("es.stream.id", streamId);

        var (state, _) = TDecider.Init(parameters);

        activity?.SetStatus(ActivityStatusCode.Ok);
        return new ResolvingAggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>(
            store, streamId, parameters, state, 0, threadSafe);
    }

    /// <summary>
    /// Loads an aggregate by replaying its event stream from the store.
    /// </summary>
    /// <remarks>
    /// Stored events are already validated facts — they are replayed through
    /// <c>Transition</c> without re-validation via <c>Decide</c>.
    /// </remarks>
    /// <param name="store">The event store to load events from.</param>
    /// <param name="streamId">The stream identifier for this aggregate instance.</param>
    /// <param name="parameters">Initialization parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Init"/>.</param>
    /// <param name="threadSafe">
    /// When <c>true</c> (default), Handle calls are serialized via a semaphore.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<ResolvingAggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>> Load(
        EventStore<TEvent> store,
        string streamId,
        TParameters parameters,
        bool threadSafe = true,
        CancellationToken cancellationToken = default)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("ResolvingAggregate.Load");
        activity?.SetTag("es.aggregate.type", _deciderTypeName);
        activity?.SetTag("es.stream.id", streamId);

        var storedEvents = await store.LoadAsync(streamId, cancellationToken).ConfigureAwait(false);
        var (seed, _) = TDecider.Init(parameters);

        var state = seed;
        for (var i = 0; i < storedEvents.Count; i++)
        {
            (state, _) = TDecider.Transition(state, storedEvents[i].Event);
        }

        activity?.SetTag("es.event.count", storedEvents.Count);
        activity?.SetTag("es.version", storedEvents.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return new ResolvingAggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>(
            store, streamId, parameters, state, storedEvents.Count, threadSafe);
    }

    /// <summary>
    /// Validates and handles a command with automatic conflict resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On <see cref="ConcurrencyException"/>:
    /// <list type="number">
    ///     <item><description>Loads events committed since our known version</description></item>
    ///     <item><description>Replays them to compute the merged state</description></item>
    ///     <item><description>Projects our events to compute the projected state</description></item>
    ///     <item><description>Calls <c>ResolveConflicts(mergedState, projectedState, ourEvents, theirEvents)</c></description></item>
    ///     <item><description>On <c>Ok</c>: transitions resolved events and retries the append</description></item>
    ///     <item><description>On <c>Err(ConflictNotResolved)</c>: wraps reason into <see cref="ConcurrencyException"/> and throws</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="command">The command to validate and handle.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The new state on success, or an error on validation failure.</returns>
    /// <exception cref="ConcurrencyException">
    /// Thrown when the conflict cannot be resolved or all retry attempts are exhausted.
    /// </exception>
    public ValueTask<Result<TState, TError>> Handle(
        TCommand command,
        CancellationToken cancellationToken = default)
    {
        if (_threadSafe)
        {
            var waitTask = _gate.WaitAsync(cancellationToken);
            if (waitTask.IsCompletedSuccessfully)
                return HandleAfterGate(command, cancellationToken);

            return AwaitGateThenHandle(waitTask, command, cancellationToken);
        }

        return HandleCore(command, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<TState, TError>> AwaitGateThenHandle(
        Task waitTask, TCommand command, CancellationToken cancellationToken)
    {
        await waitTask.ConfigureAwait(false);
        try
        {
            return await HandleCore(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ValueTask<Result<TState, TError>> HandleAfterGate(
        TCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var coreTask = HandleCore(command, cancellationToken);
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
    private async ValueTask<Result<TState, TError>> AwaitCoreThenRelease(
        ValueTask<Result<TState, TError>> coreTask)
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
    private async ValueTask<Result<TState, TError>> HandleCore(
        TCommand command, CancellationToken cancellationToken)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("ResolvingAggregate.Handle");
        activity?.SetTag("es.aggregate.type", _deciderTypeName);
        activity?.SetTag("es.stream.id", _streamId);
        activity?.SetTag("es.command.type", command?.GetType().Name);

        try
        {
            // 1. Decide (synchronous, pure)
            var decided = TDecider.Decide(_state, command);
            if (decided.IsErr)
            {
                var error = decided.Error;
                activity?.SetTag("es.result", "error");
                activity?.SetTag("es.error.type", error?.GetType().Name);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return Result<TState, TError>.Err(error);
            }

            var events = decided.Value;
            if (events.Length == 0)
            {
                activity?.SetTag("es.result", "ok");
                activity?.SetTag("es.event.count", 0);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return Result<TState, TError>.Ok(_state);
            }

            // 2. Attempt append with conflict resolution retry loop
            return await AppendWithResolution(events, activity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Concurrency conflict (retries exhausted or not resolvable)");
            throw;
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<TState, TError>> AppendWithResolution(
        TEvent[] events,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var currentEvents = events;
        var currentState = _state;
        var currentVersion = _version;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            // Transition in isolation (synchronous, pure)
            var newState = currentState;
            var newEffects = new List<TEffect>(currentEvents.Length);

            for (var i = 0; i < currentEvents.Length; i++)
            {
                var (transitioned, effect) = TDecider.Transition(newState, currentEvents[i]);
                newState = transitioned;
                newEffects.Add(effect);
            }

            try
            {
                // Attempt append
                await _store.AppendAsync(_streamId, currentEvents, currentVersion, cancellationToken)
                    .ConfigureAwait(false);

                // Success — commit state
                _state = newState;
                _version = currentVersion + currentEvents.Length;
                _effects.AddRange(newEffects);

                activity?.SetTag("es.result", "ok");
                activity?.SetTag("es.event.count", currentEvents.Length);
                activity?.SetTag("es.new_version", _version);
                activity?.SetTag("es.conflict.retries", attempt);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return Result<TState, TError>.Ok(_state);
            }
            catch (ConcurrencyException) when (attempt < MaxRetries)
            {
                // Conflict detected — attempt resolution
                activity?.AddEvent(new ActivityEvent("es.conflict.retry",
                    tags: new ActivityTagsCollection
                    {
                        { "es.conflict.attempt", attempt + 1 },
                        { "es.conflict.our_version", currentVersion }
                    }));

                // Load events committed since our known version
                var theirStoredEvents = await _store
                    .LoadAsync(_streamId, currentVersion, cancellationToken)
                    .ConfigureAwait(false);

                // Extract raw events for the domain function
                var theirEvents = new TEvent[theirStoredEvents.Count];
                for (var i = 0; i < theirStoredEvents.Count; i++)
                {
                    theirEvents[i] = theirStoredEvents[i].Event;
                }

                // Replay their events to compute merged state
                var mergedState = currentState;
                for (var i = 0; i < theirEvents.Length; i++)
                {
                    (mergedState, _) = TDecider.Transition(mergedState, theirEvents[i]);
                }

                // Project our events onto merged state (so the domain doesn't have to)
                var projectedState = mergedState;
                for (var i = 0; i < currentEvents.Length; i++)
                {
                    (projectedState, _) = TDecider.Transition(projectedState, currentEvents[i]);
                }

                // Domain-level conflict resolution (compile-time dispatch, no reflection)
                // Returns Result — no exceptions for expected domain outcomes
                var resolution = TDecider.ResolveConflicts(
                    mergedState, projectedState, currentEvents, theirEvents);

                if (resolution.IsErr)
                {
                    // Domain says conflict is irreconcilable — convert to infrastructure exception
                    // at the boundary (caller already expects ConcurrencyException)
                    var reason = resolution.Error;
                    activity?.SetStatus(ActivityStatusCode.Error,
                        $"Conflict not resolvable: {reason.Reason}");
                    throw new ConcurrencyException(_streamId, currentVersion,
                        currentVersion + theirStoredEvents.Count);
                }

                currentEvents = resolution.Value;
                currentState = mergedState;
                currentVersion += theirStoredEvents.Count;
            }
            // ConcurrencyException on final attempt falls through to re-throw
        }

        // All retries exhausted — load final state for accurate exception
        var finalEvents = await _store.LoadAsync(_streamId, _version, cancellationToken)
            .ConfigureAwait(false);
        var actualVersion = _version + finalEvents.Count;
        throw new ConcurrencyException(_streamId, _version, actualVersion);
    }

    /// <summary>
    /// Rebuilds state from scratch by replaying all stored events.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<TState> Rebuild(CancellationToken cancellationToken = default)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("ResolvingAggregate.Rebuild");
        activity?.SetTag("es.aggregate.type", _deciderTypeName);
        activity?.SetTag("es.stream.id", _streamId);

        var storedEvents = await _store.LoadAsync(_streamId, cancellationToken).ConfigureAwait(false);
        var (seed, _) = TDecider.Init(_parameters);

        var state = seed;
        for (var i = 0; i < storedEvents.Count; i++)
        {
            (state, _) = TDecider.Transition(state, storedEvents[i].Event);
        }

        if (_threadSafe)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _state = state;
                _version = storedEvents.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
        else
        {
            _state = state;
            _version = storedEvents.Count;
        }

        activity?.SetTag("es.event.count", storedEvents.Count);
        activity?.SetTag("es.version", storedEvents.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return _state;
    }

    /// <summary>
    /// Disposes the semaphore used for thread safety.
    /// </summary>
    public void Dispose() => _gate.Dispose();
}
