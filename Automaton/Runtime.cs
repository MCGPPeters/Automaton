// =============================================================================
// Automaton Runtime
// =============================================================================
// The shared runtime abstraction underlying MVU, Event Sourcing, and Actors.
//
// Mathematically, the runtime is a monadic left fold over an event stream:
//
//     foldM : (State -> Event -> M (State, Effect)) -> State -> [Event] -> M State
//
// It is parameterized by two extension points:
//
// 1. Observer  — sees each (State, Event, Effect) triple after transition.
//                Returns Result<Unit, PipelineError> to propagate errors as values.
//                Used for rendering (MVU), persisting (ES), or logging.
//
// 2. Interpreter — converts effects into feedback events.
//                   Returns Result<Events, PipelineError> to propagate errors as values.
//                   Used for effect handling / command execution.
//
// Both Observer and Interpreter form monadic pipelines: they compose via
// standard FP combinators (Then, Where, Select, Catch, Combine) using
// C#/.NET naming conventions (LINQ-style Where/Select).
//
// Every specialized runtime (MVU, ES, Actor) is an instance of this
// structure with specific Observer and Interpreter implementations.
//
// Thread safety:
//     All public entry points (Dispatch, InterpretEffect, Start) are serialized
//     via a SemaphoreSlim. Concurrent callers are queued, never interleaved.
//     Reading State or Events while a Dispatch is in-flight is safe but may
//     observe intermediate values.
//
// Feedback depth:
//     Interpreter feedback loops (effect → events → effect → …) are bounded
//     by MaxFeedbackDepth (default 64). Exceeding this throws
//     InvalidOperationException to prevent stack overflows from runaway loops.
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Automaton;

// =============================================================================
// Pipeline Error — structured error for Observer and Interpreter failures
// =============================================================================

/// <summary>
/// A structured error from an Observer or Interpreter pipeline stage.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline errors are values, not exceptions. They propagate through the
/// dispatch chain via <see cref="Result{TSuccess,TError}"/>, giving callers
/// structured, composable error handling.
/// </para>
/// <para>
/// Use <see cref="ObserverExtensions.Catch"/> or <see cref="InterpreterExtensions.Catch"/>
/// combinators to recover from specific errors without breaking the pipeline.
/// </para>
/// </remarks>
/// <param name="Message">Human-readable description of the failure.</param>
/// <param name="Source">The pipeline stage that produced the error (e.g., "persist", "render").</param>
/// <param name="Exception">The underlying exception, if the error originated from a caught exception.</param>
public readonly record struct PipelineError(
    string Message,
    string? Source = null,
    Exception? Exception = null)
{
    /// <inheritdoc/>
    public override string ToString() =>
        Source is not null ? $"[{Source}] {Message}" : Message;
}

/// <summary>
/// Observes each transition triple (state, event, effect) after the automaton steps.
/// </summary>
/// <remarks>
/// <para>
/// The observer is the extension point for side effects that depend on the
/// transition result: rendering a view (MVU), persisting an event (ES),
/// or logging an audit trail.
/// </para>
/// <para>
/// Returns <c>Result&lt;Unit, PipelineError&gt;</c> instead of throwing exceptions.
/// Errors propagate as values through the dispatch pipeline — callers receive
/// a structured <see cref="PipelineError"/> and can decide how to handle it.
/// </para>
/// <para>
/// Returns <see cref="ValueTask{TResult}"/> to avoid heap allocation for synchronous
/// implementations (the common case).
/// </para>
/// </remarks>
/// <typeparam name="TState">The state produced by the transition.</typeparam>
/// <typeparam name="TEvent">The event that triggered the transition.</typeparam>
/// <typeparam name="TEffect">The effect produced by the transition.</typeparam>
public delegate ValueTask<Result<Unit, PipelineError>> Observer<in TState, in TEvent, in TEffect>(
    TState state,
    TEvent @event,
    TEffect effect);

/// <summary>
/// Interprets an effect by converting it into zero or more feedback events.
/// </summary>
/// <remarks>
/// <para>
/// The interpreter is the extension point for effect execution. Feedback
/// events are dispatched back into the automaton, creating a closed loop.
/// Return an empty array for fire-and-forget effects.
/// </para>
/// <para>
/// Returns <c>Result&lt;TEvent[], PipelineError&gt;</c> instead of throwing exceptions.
/// Errors propagate as values — callers receive a structured <see cref="PipelineError"/>
/// and can decide how to handle it.
/// </para>
/// <para>
/// Returns <see cref="ValueTask{TResult}"/> to avoid heap allocation when
/// returning synchronously (the common case — most interpreters return
/// an empty array without awaiting).
/// </para>
/// </remarks>
/// <typeparam name="TEffect">The effect to interpret.</typeparam>
/// <typeparam name="TEvent">The feedback events produced by interpretation.</typeparam>
public delegate ValueTask<Result<TEvent[], PipelineError>> Interpreter<in TEffect, TEvent>(TEffect effect);

// =============================================================================
// Cached Result values for the fast path (avoid allocating new Result per call)
// =============================================================================

/// <summary>
/// Pre-allocated Result values for common pipeline outcomes.
/// Avoids allocating a new <c>Result&lt;Unit, PipelineError&gt;</c> on every observer call.
/// </summary>
public static class PipelineResult
{
    /// <summary>
    /// A completed ValueTask containing Ok(Unit) — the happy path for observers.
    /// </summary>
    public static readonly ValueTask<Result<Unit, PipelineError>> Ok =
        new(Result<Unit, PipelineError>.Ok(Unit.Value));
}

/// <summary>
/// The shared automaton runtime: a monadic left fold with Observer and Interpreter.
/// </summary>
/// <remarks>
/// <para>
/// This is the structural core from which MVU, Event Sourcing, and the Actor Model
/// are derived. Each specialized runtime constructs an <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect}"/>
/// with domain-specific Observer and Interpreter implementations.
/// </para>
/// <para>
/// <b>Thread safety:</b> All public mutating methods (<see cref="Dispatch"/>,
/// <see cref="InterpretEffect"/>) are serialized via a <see cref="SemaphoreSlim"/>.
/// Concurrent callers are queued and never interleaved. The <see cref="State"/>
/// and <see cref="Events"/> properties may be read concurrently but may observe
/// intermediate values during an in-flight dispatch.
/// </para>
/// <para>
/// <b>Feedback depth:</b> Interpreter feedback loops are bounded by
/// <see cref="MaxFeedbackDepth"/>. Exceeding this limit throws
/// <see cref="InvalidOperationException"/> to prevent stack overflows.
/// </para>
/// <para>
/// <b>Error propagation:</b> Observer and Interpreter return
/// <c>Result&lt;T, PipelineError&gt;</c>. Errors short-circuit the pipeline and
/// propagate to the caller as values, not exceptions.
/// </para>
/// <example>
/// <code>
/// // Create a runtime with logging observer and no-op interpreter
/// Observer&lt;ThermostatState, ThermostatEvent, ThermostatEffect&gt; log =
///     (state, @event, effect) =&gt;
///     {
///         Console.WriteLine($"{@event} → {state}");
///         return PipelineResult.Ok;
///     };
///
/// Interpreter&lt;ThermostatEffect, ThermostatEvent&gt; noOp =
///     _ =&gt; new ValueTask&lt;Result&lt;ThermostatEvent[], PipelineError&gt;&gt;(
///         Result&lt;ThermostatEvent[], PipelineError&gt;.Ok([]));
///
/// var runtime = await AutomatonRuntime&lt;Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect&gt;
///     .Start(log, noOp);
///
/// var result = await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
/// // result.IsOk == true, runtime.State.CurrentTemp == 18
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TAutomaton">The automaton type providing Init and Transition.</typeparam>
/// <typeparam name="TState">The state of the automaton.</typeparam>
/// <typeparam name="TEvent">The events that drive transitions.</typeparam>
/// <typeparam name="TEffect">The effects produced by transitions.</typeparam>
public sealed class AutomatonRuntime<TAutomaton, TState, TEvent, TEffect> : IDisposable
    where TAutomaton : Automaton<TState, TEvent, TEffect>
{
    /// <summary>
    /// Maximum recursion depth for interpreter feedback loops.
    /// Prevents stack overflow from runaway effect → event → effect chains.
    /// </summary>
    public const int MaxFeedbackDepth = 64;

    // ── Cached type names for tracing (avoid per-dispatch reflection) ──
    private static readonly string _automatonTypeName = typeof(TAutomaton).Name;
    private static readonly string _stateTypeName = typeof(TState).Name;

    private TState _state;
    private readonly Observer<TState, TEvent, TEffect> _observer;
    private readonly Interpreter<TEffect, TEvent> _interpreter;
    private readonly List<TEvent>? _events;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _threadSafe;

    /// <summary>
    /// The current state of the automaton.
    /// </summary>
    public TState State => _state;

    /// <summary>
    /// All events dispatched during the lifetime of this runtime (including feedback events).
    /// Returns an empty list when event tracking is disabled.
    /// </summary>
    public IReadOnlyList<TEvent> Events => _events ?? (IReadOnlyList<TEvent>)Array.Empty<TEvent>();

    // ── Internal API for DecidingRuntime (atomic Handle) ──────────────
    internal SemaphoreSlim Gate => _gate;
    internal bool IsThreadSafe => _threadSafe;

    /// <summary>
    /// Creates a runtime with the given initial state, observer, and interpreter.
    /// </summary>
    /// <remarks>
    /// Use the constructor when you need to control initialization yourself
    /// (e.g., rendering an initial view before interpreting init effects).
    /// Use <see cref="Start"/> for the common case where init effects should
    /// be interpreted immediately.
    /// </remarks>
    /// <param name="initialState">Initial state for the automaton.</param>
    /// <param name="observer">Observer called after each transition.</param>
    /// <param name="interpreter">Interpreter that converts effects to feedback events.</param>
    /// <param name="threadSafe">
    /// When <c>true</c> (default), all public entry points are serialized via a semaphore.
    /// Set to <c>false</c> for single-threaded scenarios (actors, UI loops, benchmarks)
    /// to eliminate semaphore overhead.
    /// </param>
    /// <param name="trackEvents">
    /// When <c>true</c> (default), all dispatched events are recorded in <see cref="Events"/>.
    /// Set to <c>false</c> to eliminate event-list overhead on the hot path.
    /// </param>
    public AutomatonRuntime(
        TState initialState,
        Observer<TState, TEvent, TEffect> observer,
        Interpreter<TEffect, TEvent> interpreter,
        bool threadSafe = true,
        bool trackEvents = true)
    {
        _state = initialState;
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        _threadSafe = threadSafe;
        _events = trackEvents ? [] : null;
    }

    /// <summary>
    /// Creates and starts a runtime, interpreting init effects immediately.
    /// </summary>
    /// <param name="observer">Observer called after each transition.</param>
    /// <param name="interpreter">Interpreter that converts effects to feedback events.</param>
    /// <param name="threadSafe">
    /// When <c>true</c> (default), all public entry points are serialized via a semaphore.
    /// Set to <c>false</c> for single-threaded scenarios.
    /// </param>
    /// <param name="trackEvents">
    /// When <c>true</c> (default), all dispatched events are recorded in <see cref="Events"/>.
    /// Set to <c>false</c> to eliminate event-list overhead on the hot path.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>> Start(
        Observer<TState, TEvent, TEffect> observer,
        Interpreter<TEffect, TEvent> interpreter,
        bool threadSafe = true,
        bool trackEvents = true,
        CancellationToken cancellationToken = default)
    {
        using var activity = AutomatonDiagnostics.Source.StartActivity("Automaton.Start");
        activity?.SetTag("automaton.type", _automatonTypeName);
        activity?.SetTag("automaton.state.type", _stateTypeName);

        var (state, effect) = TAutomaton.Init();
        var runtime = new AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>(state, observer, interpreter, threadSafe, trackEvents);
        await runtime.InterpretEffect(effect, cancellationToken).ConfigureAwait(false);

        activity?.SetStatus(ActivityStatusCode.Ok);
        return runtime;
    }

    /// <summary>
    /// Dispatches an event: transition → observe → interpret effects.
    /// Returns <c>Ok(Unit)</c> on success or <c>Err(PipelineError)</c> if the observer
    /// or interpreter pipeline reports a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is thread-safe. Concurrent calls are serialized via a semaphore.
    /// </para>
    /// <para>
    /// The transition function is pure and cannot fail. If the observer or interpreter
    /// returns <c>Err</c>, the state has already advanced (the transition is committed)
    /// and the error is propagated to the caller as a value.
    /// </para>
    /// </remarks>
    /// <param name="event">The event to dispatch.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public ValueTask<Result<Unit, PipelineError>> Dispatch(TEvent @event, CancellationToken cancellationToken = default)
    {
        var activity = AutomatonDiagnostics.Source.StartActivity("Automaton.Dispatch");
        activity?.SetTag("automaton.type", _automatonTypeName);
        activity?.SetTag("automaton.event.type", @event?.GetType().Name);

        if (_threadSafe)
        {
            var waitTask = _gate.WaitAsync(cancellationToken);
            if (waitTask.IsCompletedSuccessfully)
                return DispatchAfterGate(@event, cancellationToken, activity);

            return AwaitGateThenDispatch(waitTask, @event, cancellationToken, activity);
        }

        // Unserialized — no gate acquisition/release
        return DispatchUnserialized(@event, cancellationToken, activity);
    }

    private ValueTask<Result<Unit, PipelineError>> DispatchUnserialized(
        TEvent @event, CancellationToken cancellationToken, Activity? activity)
    {
        try
        {
            var coreTask = DispatchCore(@event, cancellationToken);
            if (coreTask.IsCompletedSuccessfully)
            {
                var result = coreTask.Result;
                if (result.IsOk)
                    activity?.SetStatus(ActivityStatusCode.Ok);
                else
                    activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
                activity?.Dispose();
                return new ValueTask<Result<Unit, PipelineError>>(result);
            }

            return AwaitCoreUnserialized(coreTask, activity);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.Dispose();
            throw;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<Unit, PipelineError>> AwaitCoreUnserialized(
        ValueTask<Result<Unit, PipelineError>> coreTask, Activity? activity)
    {
        using var _ = activity;
        try
        {
            var result = await coreTask.ConfigureAwait(false);
            if (result.IsOk)
                activity?.SetStatus(ActivityStatusCode.Ok);
            else
                activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private ValueTask<Result<Unit, PipelineError>> DispatchAfterGate(
        TEvent @event, CancellationToken cancellationToken, Activity? activity)
    {
        try
        {
            var coreTask = DispatchCore(@event, cancellationToken);
            if (coreTask.IsCompletedSuccessfully)
            {
                var result = coreTask.Result;
                if (result.IsOk)
                    activity?.SetStatus(ActivityStatusCode.Ok);
                else
                    activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
                activity?.Dispose();
                _gate.Release();
                return new ValueTask<Result<Unit, PipelineError>>(result);
            }

            return AwaitCoreThenRelease(coreTask, activity);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.Dispose();
            _gate.Release();
            throw;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<Unit, PipelineError>> AwaitCoreThenRelease(
        ValueTask<Result<Unit, PipelineError>> coreTask, Activity? activity)
    {
        using var _ = activity;
        try
        {
            var result = await coreTask.ConfigureAwait(false);
            if (result.IsOk)
                activity?.SetStatus(ActivityStatusCode.Ok);
            else
                activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<Unit, PipelineError>> AwaitGateThenDispatch(
        Task waitTask, TEvent @event, CancellationToken cancellationToken, Activity? activity)
    {
        using var _ = activity;
        await waitTask.ConfigureAwait(false);
        try
        {
            var result = await DispatchCore(@event, cancellationToken).ConfigureAwait(false);
            if (result.IsOk)
                activity?.SetStatus(ActivityStatusCode.Ok);
            else
                activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Interprets an effect, dispatching any feedback events back into the loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is an advanced API for building custom runtimes that need to control
    /// initialization order (e.g., rendering an initial view before interpreting
    /// init effects). For normal usage, prefer <see cref="Dispatch"/> or
    /// <see cref="Start"/>.
    /// </para>
    /// <para>
    /// This method is thread-safe. Concurrent calls are serialized via a semaphore.
    /// Feedback loops are bounded by <see cref="MaxFeedbackDepth"/>.
    /// </para>
    /// </remarks>
    /// <param name="effect">The effect to interpret.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public ValueTask InterpretEffect(TEffect effect, CancellationToken cancellationToken = default)
    {
        var activity = AutomatonDiagnostics.Source.StartActivity("Automaton.InterpretEffect");
        activity?.SetTag("automaton.type", _automatonTypeName);
        activity?.SetTag("automaton.effect.type", effect?.GetType().Name);

        if (_threadSafe)
        {
            var waitTask = _gate.WaitAsync(cancellationToken);
            if (waitTask.IsCompletedSuccessfully)
                return InterpretEffectAfterGate(effect, cancellationToken, activity);

            return AwaitGateThenInterpret(waitTask, effect, cancellationToken, activity);
        }

        // Unserialized — no gate acquisition/release
        return InterpretEffectUnserialized(effect, cancellationToken, activity);
    }

    private ValueTask InterpretEffectUnserialized(TEffect effect, CancellationToken cancellationToken, Activity? activity)
    {
        try
        {
            var coreTask = InterpretEffectCore(effect, cancellationToken);
            if (coreTask.IsCompletedSuccessfully)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.Dispose();
                return ValueTask.CompletedTask;
            }

            return AwaitInterpretCoreUnserialized(coreTask, activity);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.Dispose();
            throw;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AwaitInterpretCoreUnserialized(ValueTask coreTask, Activity? activity)
    {
        using var _ = activity;
        try
        {
            await coreTask.ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private ValueTask InterpretEffectAfterGate(TEffect effect, CancellationToken cancellationToken, Activity? activity)
    {
        try
        {
            var coreTask = InterpretEffectCore(effect, cancellationToken);
            if (coreTask.IsCompletedSuccessfully)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.Dispose();
                _gate.Release();
                return ValueTask.CompletedTask;
            }

            return AwaitInterpretCoreThenRelease(coreTask, activity);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.Dispose();
            _gate.Release();
            throw;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AwaitInterpretCoreThenRelease(ValueTask coreTask, Activity? activity)
    {
        using var _ = activity;
        try
        {
            await coreTask.ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AwaitGateThenInterpret(
        Task waitTask, TEffect effect, CancellationToken cancellationToken, Activity? activity)
    {
        using var _ = activity;
        await waitTask.ConfigureAwait(false);
        try
        {
            await InterpretEffectCore(effect, cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the current state without triggering a transition or observer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used by Event Sourcing to hydrate state from a replayed event stream,
    /// or by Actors for supervision/restart strategies.
    /// </para>
    /// <para>
    /// When <c>threadSafe</c> is <c>true</c>, this method acquires the gate before
    /// writing state, ensuring no concurrent dispatch can observe a partial update.
    /// Do NOT call this method from within an observer or interpreter callback —
    /// it will deadlock (the gate is already held by the dispatch in progress).
    /// </para>
    /// </remarks>
    public void Reset(TState state)
    {
        if (_threadSafe)
        {
            _gate.Wait();
            try
            { _state = state; }
            finally { _gate.Release(); }
        }
        else
        {
            _state = state;
        }
    }

    /// <summary>
    /// Disposes the semaphore used for thread safety.
    /// </summary>
    public void Dispose() => _gate.Dispose();

    // =========================================================================
    // Internal — unlocked dispatch/interpret for use within the semaphore
    // =========================================================================

    /// <summary>
    /// Dispatches an event without acquiring the gate.
    /// Must only be called while the gate is held.
    /// Used by <see cref="DecidingRuntime{TDecider,TState,TCommand,TEvent,TEffect,TError}"/>
    /// to dispatch all events from a single Decide call atomically.
    /// </summary>
    internal ValueTask<Result<Unit, PipelineError>> DispatchUnlocked(
        TEvent @event, CancellationToken cancellationToken, int depth = 0)
        => DispatchCore(@event, cancellationToken, depth);

    // =========================================================================
    // Async elision
    // =========================================================================
    // The methods below avoid allocating an async state machine on the heap
    // when the observer and interpreter complete synchronously — the common
    // case for in-memory observers and no-op / empty interpreters.
    //
    // Pattern: check ValueTask.IsCompletedSuccessfully, return ValueTask
    // directly on the fast path (zero-alloc), fall back to an async helper
    // on the slow path.
    //
    // Observer and Interpreter now return Result<T, PipelineError>. The fast
    // path checks both IsCompletedSuccessfully AND IsOk before continuing,
    // short-circuiting on Err to propagate pipeline errors as values.
    // =========================================================================

    private ValueTask<Result<Unit, PipelineError>> DispatchCore(
        TEvent @event, CancellationToken cancellationToken, int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _events?.Add(@event);

        var (newState, effect) = TAutomaton.Transition(_state, @event);
        _state = newState;

        var observerTask = _observer(_state, @event, effect);
        if (observerTask.IsCompletedSuccessfully)
        {
            var observerResult = observerTask.Result;
            if (observerResult.IsErr)
                return new ValueTask<Result<Unit, PipelineError>>(observerResult);

            return InterpretEffectCoreWithResult(effect, cancellationToken, depth);
        }

        return AwaitObserverThenInterpret(observerTask, effect, cancellationToken, depth);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<Unit, PipelineError>> AwaitObserverThenInterpret(
        ValueTask<Result<Unit, PipelineError>> observerTask, TEffect effect,
        CancellationToken cancellationToken, int depth)
    {
        var observerResult = await observerTask.ConfigureAwait(false);
        if (observerResult.IsErr)
            return observerResult;

        return await InterpretEffectCoreWithResult(effect, cancellationToken, depth).ConfigureAwait(false);
    }

    /// <summary>
    /// Interprets an effect and returns Result — used by DispatchCore pipeline.
    /// </summary>
    private ValueTask<Result<Unit, PipelineError>> InterpretEffectCoreWithResult(
        TEffect effect, CancellationToken cancellationToken, int depth = 0)
    {
        if (depth > MaxFeedbackDepth)
            throw new InvalidOperationException(
                $"Interpreter feedback loop exceeded maximum depth of {MaxFeedbackDepth}. " +
                "This usually indicates an infinite feedback cycle where an effect always " +
                "produces events whose transitions produce the same effect.");

        cancellationToken.ThrowIfCancellationRequested();

        var interpreterTask = _interpreter(effect);
        if (interpreterTask.IsCompletedSuccessfully)
        {
            var interpreterResult = interpreterTask.Result;
            if (interpreterResult.IsErr)
                return new ValueTask<Result<Unit, PipelineError>>(
                    Result<Unit, PipelineError>.Err(interpreterResult.Error));

            return DispatchFeedbackEventsWithResult(interpreterResult.Value, cancellationToken, depth);
        }

        return AwaitInterpreterThenDispatchWithResult(interpreterTask, cancellationToken, depth);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<Unit, PipelineError>> AwaitInterpreterThenDispatchWithResult(
        ValueTask<Result<TEvent[], PipelineError>> interpreterTask,
        CancellationToken cancellationToken, int depth)
    {
        var interpreterResult = await interpreterTask.ConfigureAwait(false);
        if (interpreterResult.IsErr)
            return Result<Unit, PipelineError>.Err(interpreterResult.Error);

        return await DispatchFeedbackEventsWithResult(interpreterResult.Value, cancellationToken, depth)
            .ConfigureAwait(false);
    }

    private ValueTask<Result<Unit, PipelineError>> DispatchFeedbackEventsWithResult(
        TEvent[] feedbackEvents,
        CancellationToken cancellationToken, int depth)
    {
        if (feedbackEvents.Length == 0)
            return PipelineResult.Ok;

        return DispatchFeedbackEventsWithResultAsync(feedbackEvents, cancellationToken, depth);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<Unit, PipelineError>> DispatchFeedbackEventsWithResultAsync(
        TEvent[] feedbackEvents,
        CancellationToken cancellationToken, int depth)
    {
        for (var i = 0; i < feedbackEvents.Length; i++)
        {
            var result = await DispatchCore(feedbackEvents[i], cancellationToken, depth + 1)
                .ConfigureAwait(false);
            if (result.IsErr)
                return result;
        }

        return Result<Unit, PipelineError>.Ok(Unit.Value);
    }

    /// <summary>
    /// Interprets an effect for the public InterpretEffect method (returns void-ValueTask).
    /// Delegates to the Result-returning version and unwraps.
    /// </summary>
    private ValueTask InterpretEffectCore(TEffect effect, CancellationToken cancellationToken, int depth = 0)
    {
        var resultTask = InterpretEffectCoreWithResult(effect, cancellationToken, depth);
        if (resultTask.IsCompletedSuccessfully)
            return ValueTask.CompletedTask;

        return AwaitInterpretEffectCoreUnwrap(resultTask);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask AwaitInterpretEffectCoreUnwrap(
        ValueTask<Result<Unit, PipelineError>> resultTask)
    {
        // InterpretEffect (public) ignores errors — they propagate via Dispatch
        await resultTask.ConfigureAwait(false);
    }
}

// =============================================================================
// Observer combinators — standard FP pipeline composition
// =============================================================================

/// <summary>
/// Combinators for composing observers into monadic pipelines.
/// </summary>
/// <remarks>
/// <para>
/// Observers form a <b>monoid</b> under <see cref="Then{TState,TEvent,TEffect}"/> (sequential composition)
/// and a <b>monad</b> under <see cref="Result{TSuccess,TError}"/> (railway-oriented
/// error propagation). The combinator vocabulary uses C#/.NET naming conventions.
/// </para>
/// <para>
/// <list type="table">
///     <listheader><term>Combinator</term><description>FP Concept</description></listheader>
///     <item><term><see cref="Then{TState,TEvent,TEffect}"/></term><description>Kleisli composition (>>=) — sequential, short-circuits on Err</description></item>
///     <item><term><see cref="Where{TState,TEvent,TEffect}"/></term><description>Guard / filter — skip when predicate is false</description></item>
///     <item><term><see cref="Select{TState2,TEvent2,TEffect2,TState1,TEvent1,TEffect1}"/></term><description>Contravariant functor (contramap) — transform the input triple</description></item>
///     <item><term><see cref="Catch{TState,TEvent,TEffect}"/></term><description>Error recovery — handle PipelineError and resume</description></item>
///     <item><term><see cref="Combine{TState,TEvent,TEffect}"/></term><description>Applicative — both run, first error wins</description></item>
/// </list>
/// </para>
/// </remarks>
public static class ObserverExtensions
{
    /// <summary>
    /// Composes two observers sequentially: <paramref name="first"/> runs, then <paramref name="second"/>.
    /// Short-circuits on error — if <paramref name="first"/> returns <c>Err</c>,
    /// <paramref name="second"/> is never called.
    /// </summary>
    /// <remarks>
    /// This is Kleisli composition (>>=) in the Result monad.
    /// Uses async elision: when both observers complete synchronously (the common case),
    /// no async state machine is allocated on the heap.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pipeline = persistObserver.Then(logObserver);
    /// // If persist fails, log is never called — error propagates
    /// </code>
    /// </example>
    public static Observer<TState, TEvent, TEffect> Then<TState, TEvent, TEffect>(
        this Observer<TState, TEvent, TEffect> first,
        Observer<TState, TEvent, TEffect> second) =>
        (state, @event, effect) =>
        {
            var t1 = first(state, @event, effect);
            if (t1.IsCompletedSuccessfully)
            {
                var r1 = t1.Result;
                if (r1.IsErr)
                    return new ValueTask<Result<Unit, PipelineError>>(r1);
                return second(state, @event, effect);
            }

            return AwaitFirstThenSecond(t1, second, state, @event, effect);
        };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<Unit, PipelineError>> AwaitFirstThenSecond<TState, TEvent, TEffect>(
        ValueTask<Result<Unit, PipelineError>> first,
        Observer<TState, TEvent, TEffect> second,
        TState state, TEvent @event, TEffect effect)
    {
        var r1 = await first.ConfigureAwait(false);
        if (r1.IsErr)
            return r1;
        return await second(state, @event, effect).ConfigureAwait(false);
    }

    /// <summary>
    /// Conditionally invokes the observer only when the predicate is satisfied.
    /// When the predicate returns <c>false</c>, returns <c>Ok(Unit)</c> (skip).
    /// </summary>
    /// <remarks>
    /// Analogous to LINQ <c>Where</c> — filters which transitions the observer sees.
    /// The predicate receives the full transition triple (state, event, effect).
    /// </remarks>
    /// <example>
    /// <code>
    /// // Only observe heater-related events
    /// var heaterObserver = logObserver
    ///     .Where((_, e, _) =&gt; e is ThermostatEvent.HeaterTurnedOn or ThermostatEvent.HeaterTurnedOff);
    /// </code>
    /// </example>
    public static Observer<TState, TEvent, TEffect> Where<TState, TEvent, TEffect>(
        this Observer<TState, TEvent, TEffect> observer,
        Func<TState, TEvent, TEffect, bool> predicate) =>
        (state, @event, effect) =>
            predicate(state, @event, effect)
                ? observer(state, @event, effect)
                : PipelineResult.Ok;

    /// <summary>
    /// Transforms the observer's input by applying a projection to the transition triple.
    /// This is the <b>contravariant functor</b> (contramap) operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since observers <em>consume</em> values (they are contravariant in their inputs),
    /// <c>Select</c> maps the <em>input</em> side. Given an observer of <c>(S2, E2, Eff2)</c>
    /// and a function <c>(S1, E1, Eff1) → (S2, E2, Eff2)</c>, produces an observer of
    /// <c>(S1, E1, Eff1)</c>.
    /// </para>
    /// </remarks>
    public static Observer<TState1, TEvent1, TEffect1>
        Select<TState2, TEvent2, TEffect2, TState1, TEvent1, TEffect1>(
            this Observer<TState2, TEvent2, TEffect2> observer,
            Func<TState1, TEvent1, TEffect1, (TState2 State, TEvent2 Event, TEffect2 Effect)> project) =>
        (state, @event, effect) =>
        {
            var (s2, e2, eff2) = project(state, @event, effect);
            return observer(s2, e2, eff2);
        };

    /// <summary>
    /// Recovers from an observer error by applying a handler function.
    /// If the observer returns <c>Err</c>, the handler decides whether to recover
    /// (return <c>Ok</c>) or propagate a different error.
    /// </summary>
    /// <remarks>
    /// Analogous to <c>catch</c> in exception handling, but as a pure function.
    /// The handler receives the <see cref="PipelineError"/> and can:
    /// <list type="bullet">
    ///     <item>Return <c>Ok(Unit)</c> to swallow the error and continue</item>
    ///     <item>Return <c>Err(newError)</c> to replace the error</item>
    /// </list>
    /// </remarks>
    public static Observer<TState, TEvent, TEffect> Catch<TState, TEvent, TEffect>(
        this Observer<TState, TEvent, TEffect> observer,
        Func<PipelineError, Result<Unit, PipelineError>> handler) =>
        (state, @event, effect) =>
        {
            var task = observer(state, @event, effect);
            if (task.IsCompletedSuccessfully)
            {
                var result = task.Result;
                return result.IsErr
                    ? new ValueTask<Result<Unit, PipelineError>>(handler(result.Error))
                    : task;
            }

            return AwaitThenCatch(task, handler);
        };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<Unit, PipelineError>> AwaitThenCatch(
        ValueTask<Result<Unit, PipelineError>> task,
        Func<PipelineError, Result<Unit, PipelineError>> handler)
    {
        var result = await task.ConfigureAwait(false);
        return result.IsErr ? handler(result.Error) : result;
    }

    /// <summary>
    /// Runs two observers in sequence, collecting the first error encountered.
    /// Unlike <see cref="Then{TState,TEvent,TEffect}"/>, both observers always execute — the second is NOT
    /// short-circuited by a first-observer error.
    /// </summary>
    /// <remarks>
    /// Analogous to applicative (<c>&lt;*&gt;</c>) — "run both, combine results."
    /// Useful when both observers must run (e.g., persist AND log) even if one fails.
    /// If both fail, the first error is returned.
    /// </remarks>
    public static Observer<TState, TEvent, TEffect> Combine<TState, TEvent, TEffect>(
        this Observer<TState, TEvent, TEffect> first,
        Observer<TState, TEvent, TEffect> second) =>
        (state, @event, effect) =>
        {
            var t1 = first(state, @event, effect);
            var t2 = second(state, @event, effect);

            if (t1.IsCompletedSuccessfully && t2.IsCompletedSuccessfully)
            {
                var r1 = t1.Result;
                var r2 = t2.Result;
                // First error wins
                return r1.IsErr
                    ? new ValueTask<Result<Unit, PipelineError>>(r1)
                    : new ValueTask<Result<Unit, PipelineError>>(r2);
            }

            return AwaitBothThenCombine(t1, t2);
        };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<Unit, PipelineError>> AwaitBothThenCombine(
        ValueTask<Result<Unit, PipelineError>> t1,
        ValueTask<Result<Unit, PipelineError>> t2)
    {
        var r1 = await t1.ConfigureAwait(false);
        var r2 = await t2.ConfigureAwait(false);
        return r1.IsErr ? r1 : r2;
    }
}

// =============================================================================
// Interpreter combinators — standard FP pipeline composition
// =============================================================================

/// <summary>
/// Combinators for composing interpreters into monadic pipelines.
/// </summary>
/// <remarks>
/// <para>
/// Interpreters compose similarly to observers, with the addition that their
/// success value (feedback events) can be concatenated.
/// </para>
/// <para>
/// <list type="table">
///     <listheader><term>Combinator</term><description>FP Concept</description></listheader>
///     <item><term><see cref="Then{TEffect,TEvent}"/></term><description>Kleisli composition — sequential, short-circuits on Err, concatenates events</description></item>
///     <item><term><see cref="Where{TEffect,TEvent}"/></term><description>Guard / filter — only interpret certain effects</description></item>
///     <item><term><see cref="Select{TEffect2,TEvent,TEffect1}"/></term><description>Contravariant functor — transform the effect before interpreting</description></item>
///     <item><term><see cref="Catch{TEffect,TEvent}"/></term><description>Error recovery — handle PipelineError and resume</description></item>
/// </list>
/// </para>
/// </remarks>
public static class InterpreterExtensions
{
    /// <summary>
    /// Composes two interpreters sequentially: <paramref name="first"/> runs, then <paramref name="second"/>.
    /// Feedback events from both are concatenated. Short-circuits on error.
    /// </summary>
    public static Interpreter<TEffect, TEvent> Then<TEffect, TEvent>(
        this Interpreter<TEffect, TEvent> first,
        Interpreter<TEffect, TEvent> second) =>
        effect =>
        {
            var t1 = first(effect);
            if (t1.IsCompletedSuccessfully)
            {
                var r1 = t1.Result;
                if (r1.IsErr)
                    return new ValueTask<Result<TEvent[], PipelineError>>(r1);

                var t2 = second(effect);
                if (t2.IsCompletedSuccessfully)
                {
                    var r2 = t2.Result;
                    if (r2.IsErr)
                        return new ValueTask<Result<TEvent[], PipelineError>>(r2);
                    return new ValueTask<Result<TEvent[], PipelineError>>(
                        Result<TEvent[], PipelineError>.Ok([.. r1.Value, .. r2.Value]));
                }

                return AwaitSecondInterpreter(r1.Value, t2);
            }

            return AwaitFirstThenSecondInterpreter(t1, second, effect);
        };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<TEvent[], PipelineError>> AwaitSecondInterpreter<TEvent>(
        TEvent[] firstEvents,
        ValueTask<Result<TEvent[], PipelineError>> secondTask)
    {
        var r2 = await secondTask.ConfigureAwait(false);
        return r2.IsErr
            ? r2
            : Result<TEvent[], PipelineError>.Ok([.. firstEvents, .. r2.Value]);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<TEvent[], PipelineError>> AwaitFirstThenSecondInterpreter<TEffect, TEvent>(
        ValueTask<Result<TEvent[], PipelineError>> firstTask,
        Interpreter<TEffect, TEvent> second,
        TEffect effect)
    {
        var r1 = await firstTask.ConfigureAwait(false);
        if (r1.IsErr)
            return r1;

        var r2 = await second(effect).ConfigureAwait(false);
        return r2.IsErr
            ? r2
            : Result<TEvent[], PipelineError>.Ok([.. r1.Value, .. r2.Value]);
    }

    /// <summary>
    /// Conditionally invokes the interpreter only when the predicate is satisfied.
    /// When the predicate returns <c>false</c>, returns <c>Ok([])</c> (no feedback events).
    /// </summary>
    public static Interpreter<TEffect, TEvent> Where<TEffect, TEvent>(
        this Interpreter<TEffect, TEvent> interpreter,
        Func<TEffect, bool> predicate) =>
        effect =>
            predicate(effect)
                ? interpreter(effect)
                : new ValueTask<Result<TEvent[], PipelineError>>(
                    Result<TEvent[], PipelineError>.Ok([]));

    /// <summary>
    /// Transforms the interpreter's input by applying a projection to the effect.
    /// This is the contravariant functor (contramap) operation.
    /// </summary>
    public static Interpreter<TEffect1, TEvent> Select<TEffect2, TEvent, TEffect1>(
        this Interpreter<TEffect2, TEvent> interpreter,
        Func<TEffect1, TEffect2> project) =>
        effect => interpreter(project(effect));

    /// <summary>
    /// Recovers from an interpreter error by applying a handler function.
    /// </summary>
    public static Interpreter<TEffect, TEvent> Catch<TEffect, TEvent>(
        this Interpreter<TEffect, TEvent> interpreter,
        Func<PipelineError, Result<TEvent[], PipelineError>> handler) =>
        effect =>
        {
            var task = interpreter(effect);
            if (task.IsCompletedSuccessfully)
            {
                var result = task.Result;
                return result.IsErr
                    ? new ValueTask<Result<TEvent[], PipelineError>>(handler(result.Error))
                    : task;
            }

            return AwaitInterpreterThenCatch(task, handler);
        };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<TEvent[], PipelineError>> AwaitInterpreterThenCatch<TEvent>(
        ValueTask<Result<TEvent[], PipelineError>> task,
        Func<PipelineError, Result<TEvent[], PipelineError>> handler)
    {
        var result = await task.ConfigureAwait(false);
        return result.IsErr ? handler(result.Error) : result;
    }
}
