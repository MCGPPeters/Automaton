// =============================================================================
// AggregateRunner — Event-Sourced Aggregate Runtime
// =============================================================================
// Production-grade aggregate runner implementing the decide-then-append pattern:
//
//     Command → Decide(state) → Result<Events, Error>
//         → Ok:  Transition each event → Append to store → Commit state
//         → Err: Return error, state unchanged, nothing persisted
//
// Upgrades over the reference implementation:
//     • Async event store via EventStore<TEvent> abstraction
//     • Optimistic concurrency via expected version
//     • OpenTelemetry tracing via ActivitySource
//     • Thread safety via SemaphoreSlim
//     • CancellationToken support throughout
//     • Stream-based identity for multi-aggregate scenarios
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Automaton.Patterns.EventSourcing;

/// <summary>
/// Runs a <see cref="Decider{TState,TCommand,TEvent,TEffect,TError,TParameters}"/> as an
/// event-sourced aggregate with async persistence and optimistic concurrency.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate runner implements the decide-then-append pattern:
/// <list type="number">
///     <item><description>Receive a command (user intent)</description></item>
///     <item><description>Validate via <c>Decide(state, command)</c> → events or error</description></item>
///     <item><description>On success: transition state, append events to store atomically</description></item>
///     <item><description>On failure: return error, state unchanged, nothing persisted</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Optimistic concurrency:</b> Each Handle tracks the current stream version.
/// If another process has appended events since the aggregate was loaded, the
/// event store will throw <see cref="ConcurrencyException"/>. The caller should
/// reload and retry.
/// </para>
/// <para>
/// <b>Thread safety:</b> When <c>threadSafe</c> is <c>true</c> (default),
/// Handle calls are serialized via a <see cref="SemaphoreSlim"/>. This prevents
/// concurrent Handle calls from reading the same state and both trying to append.
/// </para>
/// <para>
/// <b>Tracing:</b> All operations emit OpenTelemetry-compatible spans via
/// <see cref="EventSourcingDiagnostics.Source"/>.
/// </para>
/// <example>
/// <code>
/// var store = new InMemoryEventStore&lt;MyEvent&gt;();
/// var aggregate = await AggregateRunner&lt;MyDecider, MyState, MyCommand,
///     MyEvent, MyEffect, MyError&gt;.Create(store, "my-aggregate-1");
///
/// var result = await aggregate.Handle(new MyCommand.DoSomething());
/// // result is Ok(newState) or Err(error)
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TDecider">The Decider type providing Initialize, Decide, and Transition.</typeparam>
/// <typeparam name="TState">The aggregate state.</typeparam>
/// <typeparam name="TCommand">Commands representing user intent.</typeparam>
/// <typeparam name="TEvent">Events representing validated facts.</typeparam>
/// <typeparam name="TEffect">Effects produced by transitions.</typeparam>
/// <typeparam name="TError">Errors produced by invalid commands.</typeparam>
/// <typeparam name="TParameters">The type of parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Initialize"/>.</typeparam>
public sealed class AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters> : IDisposable
    where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
{
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

    private AggregateRunner(
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
    /// <param name="parameters">Initialization parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Initialize"/>.</param>
    /// <param name="threadSafe">
    /// When <c>true</c> (default), Handle calls are serialized via a semaphore.
    /// Set to <c>false</c> for single-threaded scenarios.
    /// </param>
    public static AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters> Create(
        EventStore<TEvent> store,
        string streamId,
        TParameters parameters,
        bool threadSafe = true)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("Aggregate.Create");
        activity?.SetTag("es.aggregate.type", _deciderTypeName);
        activity?.SetTag("es.stream.id", streamId);

        var (state, _) = TDecider.Initialize(parameters);

        activity?.SetStatus(ActivityStatusCode.Ok);
        return new AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>(
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
    /// <param name="parameters">Initialization parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Initialize"/>.</param>
    /// <param name="threadSafe">
    /// When <c>true</c> (default), Handle calls are serialized via a semaphore.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>> Load(
        EventStore<TEvent> store,
        string streamId,
        TParameters parameters,
        bool threadSafe = true,
        CancellationToken cancellationToken = default)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("Aggregate.Load");
        activity?.SetTag("es.aggregate.type", _deciderTypeName);
        activity?.SetTag("es.stream.id", streamId);

        var storedEvents = await store.LoadAsync(streamId, cancellationToken).ConfigureAwait(false);
        var (seed, _) = TDecider.Initialize(parameters);

        var state = seed;
        for (var i = 0; i < storedEvents.Count; i++)
        {
            (state, _) = TDecider.Transition(state, storedEvents[i].Event);
        }

        activity?.SetTag("es.event.count", storedEvents.Count);
        activity?.SetTag("es.version", storedEvents.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return new AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>(
            store, streamId, parameters, state, storedEvents.Count, threadSafe);
    }

    /// <summary>
    /// Validates and handles a command: Decide → Transition → Append → Commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On validation error, the aggregate state is unchanged and no events are appended.
    /// </para>
    /// <para>
    /// On success, events are computed in isolation (local state), then appended to the
    /// store with optimistic concurrency. State is only committed after a successful append,
    /// preserving the state/store invariant on partial failure.
    /// </para>
    /// <para>
    /// If the event store detects a concurrency conflict (another process appended events),
    /// a <see cref="ConcurrencyException"/> is thrown. The aggregate state remains at the
    /// pre-Handle version — the caller should reload and retry.
    /// </para>
    /// </remarks>
    /// <param name="command">The command to validate and handle.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The new state on success, or an error on validation failure.</returns>
    /// <exception cref="ConcurrencyException">
    /// Thrown when the expected stream version does not match the actual version.
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
        using var activity = EventSourcingDiagnostics.Source.StartActivity("Aggregate.Handle");
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
                // Idempotent command — accepted but nothing happened
                activity?.SetTag("es.result", "ok");
                activity?.SetTag("es.event.count", 0);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return Result<TState, TError>.Ok(_state);
            }

            // 2. Transition in isolation (synchronous, pure)
            var newState = _state;
            var newEffects = new List<TEffect>(events.Length);

            for (var i = 0; i < events.Length; i++)
            {
                var (transitioned, effect) = TDecider.Transition(newState, events[i]);
                newState = transitioned;
                newEffects.Add(effect);
            }

            // 3. Append to store (async, may throw ConcurrencyException)
            await _store.AppendAsync(_streamId, events, _version, cancellationToken)
                .ConfigureAwait(false);

            // 4. Commit state only after successful append
            _state = newState;
            _version += events.Length;
            _effects.AddRange(newEffects);

            activity?.SetTag("es.result", "ok");
            activity?.SetTag("es.event.count", events.Length);
            activity?.SetTag("es.new_version", _version);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return Result<TState, TError>.Ok(_state);
        }
        catch (ConcurrencyException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Concurrency conflict");
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

    /// <summary>
    /// Rebuilds state from scratch by replaying all stored events.
    /// </summary>
    /// <remarks>
    /// Useful for consistency checks or when the aggregate needs to be
    /// resynchronized with the event store.
    /// </remarks>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<TState> Rebuild(CancellationToken cancellationToken = default)
    {
        using var activity = EventSourcingDiagnostics.Source.StartActivity("Aggregate.Rebuild");
        activity?.SetTag("es.aggregate.type", _deciderTypeName);
        activity?.SetTag("es.stream.id", _streamId);

        var storedEvents = await _store.LoadAsync(_streamId, cancellationToken).ConfigureAwait(false);
        var (seed, _) = TDecider.Initialize(_parameters);

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
