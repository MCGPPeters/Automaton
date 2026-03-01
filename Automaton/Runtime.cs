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
//                Used for rendering (MVU), persisting (ES), or logging.
//
// 2. Interpreter — converts effects into feedback events.
//                   Used for effect handling / command execution.
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

/// <summary>
/// Observes each transition triple (state, event, effect) after the automaton steps.
/// </summary>
/// <remarks>
/// The observer is the extension point for side effects that depend on the
/// transition result: rendering a view (MVU), persisting an event (ES),
/// or logging an audit trail.
/// Implementations SHOULD NOT throw exceptions. If an observer throws after
/// a transition, the state has already advanced and the exception propagates
/// to the caller.
/// Returns <see cref="ValueTask"/> to avoid heap allocation for synchronous
/// implementations (the common case).
/// </remarks>
/// <typeparam name="TState">The state produced by the transition.</typeparam>
/// <typeparam name="TEvent">The event that triggered the transition.</typeparam>
/// <typeparam name="TEffect">The effect produced by the transition.</typeparam>
public delegate ValueTask Observer<in TState, in TEvent, in TEffect>(
    TState state,
    TEvent @event,
    TEffect effect);

/// <summary>
/// Interprets an effect by converting it into zero or more feedback events.
/// </summary>
/// <remarks>
/// The interpreter is the extension point for effect execution. Feedback
/// events are dispatched back into the automaton, creating a closed loop.
/// Return an empty array for fire-and-forget effects.
/// Implementations SHOULD NOT throw exceptions. If an interpreter throws,
/// the state has already advanced and the exception propagates to the caller.
/// Returns <see cref="ValueTask{TResult}"/> to avoid heap allocation when
/// returning synchronously (the common case — most interpreters return
/// an empty array without awaiting).
/// </remarks>
/// <typeparam name="TEffect">The effect to interpret.</typeparam>
/// <typeparam name="TEvent">The feedback events produced by interpretation.</typeparam>
public delegate ValueTask<TEvent[]> Interpreter<in TEffect, TEvent>(TEffect effect);

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
/// <example>
/// <code>
/// // Create a runtime with logging observer and no-op interpreter
/// Observer&lt;ThermostatState, ThermostatEvent, ThermostatEffect&gt; log =
///     (state, @event, effect) =&gt; { Console.WriteLine($"{@event} → {state}"); return ValueTask.CompletedTask; };
///
/// Interpreter&lt;ThermostatEffect, ThermostatEvent&gt; noOp =
///     _ =&gt; new ValueTask&lt;ThermostatEvent[]&gt;([]);
///
/// var runtime = await AutomatonRuntime&lt;Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect&gt;
///     .Start(log, noOp);
///
/// await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
/// // runtime.State.CurrentTemp == 18
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
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is thread-safe. Concurrent calls are serialized via a semaphore.
    /// </para>
    /// <para>
    /// The transition function is pure and cannot fail. If the observer or interpreter
    /// throws, the state has already advanced (the transition is committed) and the
    /// exception propagates to the caller. Callers should ensure their observer and
    /// interpreter implementations handle errors internally.
    /// </para>
    /// </remarks>
    /// <param name="event">The event to dispatch.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public ValueTask Dispatch(TEvent @event, CancellationToken cancellationToken = default)
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

    private ValueTask DispatchUnserialized(TEvent @event, CancellationToken cancellationToken, Activity? activity)
    {
        try
        {
            var coreTask = DispatchCore(@event, cancellationToken);
            if (coreTask.IsCompletedSuccessfully)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.Dispose();
                return ValueTask.CompletedTask;
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AwaitCoreUnserialized(ValueTask coreTask, Activity? activity)
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

    private ValueTask DispatchAfterGate(TEvent @event, CancellationToken cancellationToken, Activity? activity)
    {
        try
        {
            var coreTask = DispatchCore(@event, cancellationToken);
            if (coreTask.IsCompletedSuccessfully)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.Dispose();
                _gate.Release();
                return ValueTask.CompletedTask;
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AwaitCoreThenRelease(ValueTask coreTask, Activity? activity)
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
    private async ValueTask AwaitGateThenDispatch(
        Task waitTask, TEvent @event, CancellationToken cancellationToken, Activity? activity)
    {
        using var _ = activity;
        await waitTask.ConfigureAwait(false);
        try
        {
            await DispatchCore(@event, cancellationToken).ConfigureAwait(false);
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
    internal ValueTask DispatchUnlocked(TEvent @event, CancellationToken cancellationToken, int depth = 0)
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
    // =========================================================================

    private ValueTask DispatchCore(TEvent @event, CancellationToken cancellationToken, int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _events?.Add(@event);

        var (newState, effect) = TAutomaton.Transition(_state, @event);
        _state = newState;

        var observerTask = _observer(_state, @event, effect);
        if (observerTask.IsCompletedSuccessfully)
            return InterpretEffectCore(effect, cancellationToken, depth);

        return AwaitObserverThenInterpret(observerTask, effect, cancellationToken, depth);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AwaitObserverThenInterpret(
        ValueTask observerTask, TEffect effect,
        CancellationToken cancellationToken, int depth)
    {
        await observerTask.ConfigureAwait(false);
        await InterpretEffectCore(effect, cancellationToken, depth).ConfigureAwait(false);
    }

    private ValueTask InterpretEffectCore(TEffect effect, CancellationToken cancellationToken, int depth = 0)
    {
        if (depth > MaxFeedbackDepth)
            throw new InvalidOperationException(
                $"Interpreter feedback loop exceeded maximum depth of {MaxFeedbackDepth}. " +
                "This usually indicates an infinite feedback cycle where an effect always " +
                "produces events whose transitions produce the same effect.");

        cancellationToken.ThrowIfCancellationRequested();

        var interpreterTask = _interpreter(effect);
        if (interpreterTask.IsCompletedSuccessfully)
            return DispatchFeedbackEvents(interpreterTask.Result, cancellationToken, depth);

        return AwaitInterpreterThenDispatch(interpreterTask, cancellationToken, depth);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AwaitInterpreterThenDispatch(
        ValueTask<TEvent[]> interpreterTask,
        CancellationToken cancellationToken, int depth)
    {
        var feedbackEvents = await interpreterTask.ConfigureAwait(false);
        await DispatchFeedbackEvents(feedbackEvents, cancellationToken, depth).ConfigureAwait(false);
    }

    private ValueTask DispatchFeedbackEvents(
        TEvent[] feedbackEvents,
        CancellationToken cancellationToken, int depth)
    {
        if (feedbackEvents.Length == 0)
            return ValueTask.CompletedTask;

        return DispatchFeedbackEventsAsync(feedbackEvents, cancellationToken, depth);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask DispatchFeedbackEventsAsync(
        TEvent[] feedbackEvents,
        CancellationToken cancellationToken, int depth)
    {
        for (var i = 0; i < feedbackEvents.Length; i++)
        {
            await DispatchCore(feedbackEvents[i], cancellationToken, depth + 1).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Combinators for composing observers.
/// </summary>
public static class ObserverExtensions
{
    /// <summary>
    /// Composes two observers sequentially: <paramref name="first"/> runs, then <paramref name="second"/>.
    /// </summary>
    /// <remarks>
    /// Uses async elision: when both observers complete synchronously (the common case),
    /// no async state machine is allocated on the heap.
    /// </remarks>
    /// <example>
    /// <code>
    /// var combined = renderObserver.Then(logObserver);
    /// </code>
    /// </example>
    public static Observer<TState, TEvent, TEffect> Then<TState, TEvent, TEffect>(
        this Observer<TState, TEvent, TEffect> first,
        Observer<TState, TEvent, TEffect> second) =>
        (state, @event, effect) =>
        {
            var t1 = first(state, @event, effect);
            if (t1.IsCompletedSuccessfully)
                return second(state, @event, effect);

            return AwaitFirstThenSecond(t1, second, state, @event, effect);
        };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask AwaitFirstThenSecond<TState, TEvent, TEffect>(
        ValueTask first,
        Observer<TState, TEvent, TEffect> second,
        TState state, TEvent @event, TEffect effect)
    {
        await first.ConfigureAwait(false);
        await second(state, @event, effect).ConfigureAwait(false);
    }
}
