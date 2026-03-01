// =============================================================================
// Decider — Command Validation for Automatons
// =============================================================================
// The Decider pattern (Jérémie Chassaing, 2021) adds a command validation layer
// to the Automaton kernel. It separates:
//
//     intent  (Command)  →  decision  (Decide)  →  fact  (Event)  →  evolution  (Transition)
//
// Mathematically, Decide is a Kleisli arrow:
//
//     decide : Command → Reader<State, Result<Events, Error>>
//
// The Decider composes with the Automaton:
// - Automaton provides:  Transition (evolve) + Init (initial state)
// - Decider adds:        Decide (command validation) + IsTerminal (lifecycle)
//
// Together they form the seven elements of the Decider pattern:
//   1. Command type
//   2. Event type
//   3. State type
//   4. Initial state       (Init)
//   5. decide function      (Decide)
//   6. evolve function      (Transition)
//   7. isTerminal function  (IsTerminal)
//
// The same domain logic is reusable across runtimes — the Decide function is
// pure and testable independent of MVU, ES, or Actor infrastructure.
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Automaton;

/// <summary>
/// A Decider is an Automaton that validates commands before transitioning.
/// </summary>
/// <remarks>
/// <para>
/// The Decider pattern separates intent (commands) from facts (events).
/// Commands represent what the user wants to do; the <see cref="Decide"/> function
/// validates them against the current state and produces events (or rejects them).
/// Events then flow into <see cref="Automaton{TState,TEvent,TEffect}.Transition"/>
/// for pure state evolution.
/// </para>
/// <para>
/// This is the universal abstraction for command validation in event-driven systems.
/// It works identically across MVU, Event Sourcing, and Actor runtimes.
/// </para>
/// <example>
/// <code>
/// public class Thermostat
///     : Decider&lt;ThermostatState, ThermostatCommand, ThermostatEvent, ThermostatEffect, ThermostatError&gt;
/// {
///     public static (ThermostatState, ThermostatEffect) Init() =&gt;
///         (new ThermostatState(20.0m, 22.0m, false, true), new ThermostatEffect.None());
///
///     public static Result&lt;ThermostatEvent[], ThermostatError&gt; Decide(
///         ThermostatState state, ThermostatCommand command) =&gt;
///         command switch
///         {
///             ThermostatCommand.SetTarget(var target) when target &lt; 5 || target &gt; 40 =&gt;
///                 Result&lt;ThermostatEvent[], ThermostatError&gt;
///                     .Err(new ThermostatError.InvalidTarget(target, 5, 40)),
///             ThermostatCommand.SetTarget(var target) =&gt;
///                 Result&lt;ThermostatEvent[], ThermostatError&gt;
///                     .Ok([new ThermostatEvent.TargetSet(target)]),
///             _ =&gt; throw new UnreachableException()
///         };
///
///     public static (ThermostatState, ThermostatEffect) Transition(
///         ThermostatState state, ThermostatEvent @event) =&gt; ...;
/// }
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TState">The state of the automaton.</typeparam>
/// <typeparam name="TCommand">Commands representing user intent.</typeparam>
/// <typeparam name="TEvent">Events representing validated facts.</typeparam>
/// <typeparam name="TEffect">Effects produced by transitions.</typeparam>
/// <typeparam name="TError">Errors produced by invalid commands.</typeparam>
public interface Decider<TState, TCommand, TEvent, TEffect, TError>
    : Automaton<TState, TEvent, TEffect>
{
    /// <summary>
    /// Validates a command against the current state, producing events or an error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This function MUST be pure: its return value depends only on the inputs,
    /// and it produces no side effects. All external data (time, exchange rates, etc.)
    /// must be included in the command before calling Decide.
    /// </para>
    /// <para>
    /// When the command is valid, return <c>Ok</c> with the events that occurred.
    /// When invalid, return <c>Err</c> with a domain-specific error.
    /// An empty event array means "accepted but nothing happened" (idempotent command).
    /// </para>
    /// </remarks>
    static abstract Result<TEvent[], TError> Decide(TState state, TCommand command);

    /// <summary>
    /// Whether the automaton has reached a terminal state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Terminal states signal that no further commands should be processed.
    /// Used by infrastructure for lifecycle management (archiving, disposal).
    /// </para>
    /// <para>
    /// Defaults to <c>false</c> (never terminal). Override when the domain
    /// has a natural end-of-life (e.g., a game that ends, an order that ships).
    /// </para>
    /// </remarks>
    static virtual bool IsTerminal(TState state) => false;
}

/// <summary>
/// Runtime that validates commands via <see cref="Decider{TState,TCommand,TEvent,TEffect,TError}.Decide"/>
/// before dispatching events through the <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The deciding runtime composes the Decider's validation layer with the
/// shared Automaton runtime's event loop (Observer + Interpreter):
/// </para>
/// <para>
/// <c>Command → Decide(state) → Result&lt;Events, Error&gt; → [Dispatch each] → (State', Effects)</c>
/// </para>
/// <para>
/// On success, all produced events are dispatched sequentially through the
/// underlying AutomatonRuntime (triggering Observer and Interpreter as normal).
/// On error, no events are dispatched and state remains unchanged.
/// </para>
/// <example>
/// <code>
/// var runtime = await DecidingRuntime&lt;Thermostat, ThermostatState, ThermostatCommand,
///     ThermostatEvent, ThermostatEffect, ThermostatError&gt;.Start(observer, interpreter);
///
/// var result = await runtime.Handle(new ThermostatCommand.SetTarget(25m));
/// // result is Ok(ThermostatState { TargetTemp = 25 })
///
/// var invalid = await runtime.Handle(new ThermostatCommand.SetTarget(50m));
/// // invalid is Err(ThermostatError.InvalidTarget { ... })
/// // state remains unchanged
/// </code>
/// </example>
/// </remarks>
public sealed class DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError> : IDisposable
    where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError>
{
    // ── Cached type names for tracing (avoid per-Handle reflection) ──
    private static readonly string DeciderTypeName = typeof(TDecider).Name;
    private static readonly string StateTypeName = typeof(TState).Name;

    private readonly AutomatonRuntime<TDecider, TState, TEvent, TEffect> _core;

    /// <summary>
    /// The current state of the automaton.
    /// </summary>
    public TState State => _core.State;

    /// <summary>
    /// All events dispatched during the lifetime of this runtime (including feedback events).
    /// </summary>
    public IReadOnlyList<TEvent> Events => _core.Events;

    /// <summary>
    /// Whether the automaton has reached a terminal state.
    /// </summary>
    public bool IsTerminal => TDecider.IsTerminal(_core.State);

    private DecidingRuntime(AutomatonRuntime<TDecider, TState, TEvent, TEffect> core)
    {
        _core = core;
    }

    /// <summary>
    /// Creates and starts a deciding runtime, interpreting init effects immediately.
    /// </summary>
    /// <param name="observer">Observer called after each transition.</param>
    /// <param name="interpreter">Interpreter that converts effects to feedback events.</param>
    /// <param name="threadSafe">
    /// When <c>true</c> (default), Handle calls are serialized via a semaphore.
    /// Set to <c>false</c> for single-threaded scenarios to eliminate semaphore overhead.
    /// </param>
    /// <param name="trackEvents">
    /// When <c>true</c> (default), all dispatched events are recorded in <see cref="Events"/>.
    /// Set to <c>false</c> to eliminate event-list overhead on the hot path.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError>> Start(
        Observer<TState, TEvent, TEffect> observer,
        Interpreter<TEffect, TEvent> interpreter,
        bool threadSafe = true,
        bool trackEvents = true,
        CancellationToken cancellationToken = default)
    {
        using var activity = AutomatonDiagnostics.Source.StartActivity("Automaton.Decider.Start");
        activity?.SetTag("automaton.type", DeciderTypeName);
        activity?.SetTag("automaton.state.type", StateTypeName);

        var core = await AutomatonRuntime<TDecider, TState, TEvent, TEffect>
            .Start(observer, interpreter, threadSafe, trackEvents, cancellationToken).ConfigureAwait(false);

        activity?.SetStatus(ActivityStatusCode.Ok);
        return new DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError>(core);
    }

    /// <summary>
    /// Validates and handles a command: Decide → Dispatch events → return new state or error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If <see cref="Decider{TState,TCommand,TEvent,TEffect,TError}.Decide"/> returns
    /// <c>Ok(events)</c>, each event is dispatched through the underlying runtime
    /// (triggering transitions, observer, and interpreter). The final state is returned.
    /// If it returns <c>Err(error)</c>, no events are dispatched and state is unchanged.
    /// </para>
    /// <para>
    /// <b>Atomicity:</b> The entire Handle operation (Decide + all Dispatches) executes
    /// under a single gate acquisition. Concurrent Handle calls are serialized —
    /// no interleaving between a Decide reading state and its events being dispatched.
    /// This prevents TOCTOU races where another caller could modify state between
    /// Decide and Dispatch.
    /// </para>
    /// </remarks>
    /// <param name="command">The command to validate and handle.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public ValueTask<Result<TState, TError>> Handle(TCommand command, CancellationToken cancellationToken = default)
    {
        var activity = AutomatonDiagnostics.Source.StartActivity("Automaton.Decider.Handle");
        activity?.SetTag("automaton.type", DeciderTypeName);
        activity?.SetTag("automaton.command.type", command?.GetType().Name);

        if (_core.IsThreadSafe)
        {
            var waitTask = _core.Gate.WaitAsync(cancellationToken);
            if (waitTask.IsCompletedSuccessfully)
                return HandleAfterGate(command, cancellationToken, activity);

            return AwaitGateThenHandle(waitTask, command, cancellationToken, activity);
        }

        // Unserialized — no gate acquisition/release
        return HandleUnserialized(command, cancellationToken, activity);
    }

    private ValueTask<Result<TState, TError>> HandleUnserialized(
        TCommand command, CancellationToken cancellationToken, Activity? activity)
    {
        try
        {
            var decided = TDecider.Decide(_core.State, command);
            if (decided.IsOk)
            {
                return DispatchEventsAndReturnOkUnserialized(decided.Value, cancellationToken, activity);
            }
            else
            {
                var error = decided.Error;
                activity?.SetTag("automaton.result", "error");
                activity?.SetTag("automaton.error.type", error?.GetType().Name);
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.Dispose();
                return new ValueTask<Result<TState, TError>>(
                    Result<TState, TError>.Err(error));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.Dispose();
            throw;
        }
    }

    private ValueTask<Result<TState, TError>> DispatchEventsAndReturnOkUnserialized(
        TEvent[] events, CancellationToken cancellationToken, Activity? activity)
    {
        for (var i = 0; i < events.Length; i++)
        {
            var dispatchTask = _core.DispatchUnlocked(events[i], cancellationToken);
            if (!dispatchTask.IsCompletedSuccessfully)
                return AwaitRemainingEventsAndReturnOkUnserialized(dispatchTask, events, i + 1, cancellationToken, activity);
        }

        activity?.SetTag("automaton.result", "ok");
        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.Dispose();
        return new ValueTask<Result<TState, TError>>(
            Result<TState, TError>.Ok(_core.State));
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<TState, TError>> AwaitRemainingEventsAndReturnOkUnserialized(
        ValueTask pendingTask, TEvent[] events, int startIndex,
        CancellationToken cancellationToken, Activity? activity)
    {
        using var _ = activity;
        try
        {
            await pendingTask.ConfigureAwait(false);

            for (var i = startIndex; i < events.Length; i++)
            {
                await _core.DispatchUnlocked(events[i], cancellationToken).ConfigureAwait(false);
            }

            activity?.SetTag("automaton.result", "ok");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return Result<TState, TError>.Ok(_core.State);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private ValueTask<Result<TState, TError>> HandleAfterGate(
        TCommand command, CancellationToken cancellationToken, Activity? activity)
    {
        try
        {
            var decided = TDecider.Decide(_core.State, command);
            if (decided.IsOk)
            {
                return DispatchEventsAndReturnOk(decided.Value, cancellationToken, activity);
            }
            else
            {
                var error = decided.Error;
                activity?.SetTag("automaton.result", "error");
                activity?.SetTag("automaton.error.type", error?.GetType().Name);
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.Dispose();
                _core.Gate.Release();
                return new ValueTask<Result<TState, TError>>(
                    Result<TState, TError>.Err(error));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.Dispose();
            _core.Gate.Release();
            throw;
        }
    }

    private ValueTask<Result<TState, TError>> DispatchEventsAndReturnOk(
        TEvent[] events, CancellationToken cancellationToken, Activity? activity)
    {
        for (var i = 0; i < events.Length; i++)
        {
            var dispatchTask = _core.DispatchUnlocked(events[i], cancellationToken);
            if (!dispatchTask.IsCompletedSuccessfully)
                return AwaitRemainingEventsAndReturnOk(dispatchTask, events, i + 1, cancellationToken, activity);
        }

        activity?.SetTag("automaton.result", "ok");
        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.Dispose();
        _core.Gate.Release();
        return new ValueTask<Result<TState, TError>>(
            Result<TState, TError>.Ok(_core.State));
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<TState, TError>> AwaitRemainingEventsAndReturnOk(
        ValueTask pendingTask, TEvent[] events, int startIndex,
        CancellationToken cancellationToken, Activity? activity)
    {
        using var _ = activity;
        try
        {
            await pendingTask.ConfigureAwait(false);

            for (var i = startIndex; i < events.Length; i++)
            {
                await _core.DispatchUnlocked(events[i], cancellationToken).ConfigureAwait(false);
            }

            activity?.SetTag("automaton.result", "ok");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return Result<TState, TError>.Ok(_core.State);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _core.Gate.Release();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<TState, TError>> AwaitGateThenHandle(
        Task waitTask, TCommand command, CancellationToken cancellationToken, Activity? activity)
    {
        using var _ = activity;
        await waitTask.ConfigureAwait(false);
        try
        {
            var decided = TDecider.Decide(_core.State, command);
            if (decided.IsOk)
            {
                var events = decided.Value;
                for (var i = 0; i < events.Length; i++)
                {
                    await _core.DispatchUnlocked(events[i], cancellationToken).ConfigureAwait(false);
                }

                activity?.SetTag("automaton.result", "ok");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return Result<TState, TError>.Ok(_core.State);
            }
            else
            {
                var error = decided.Error;
                activity?.SetTag("automaton.result", "error");
                activity?.SetTag("automaton.error.type", error?.GetType().Name);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return Result<TState, TError>.Err(error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _core.Gate.Release();
        }
    }

    /// <summary>
    /// Replaces the current state without triggering a transition.
    /// Used for hydration from an event store or snapshot.
    /// </summary>
    /// <remarks>
    /// When <c>threadSafe</c> is <c>true</c>, this method acquires the gate before
    /// writing state. Do NOT call from within a Handle callback — it will deadlock.
    /// </remarks>
    public void Reset(TState state) => _core.Reset(state);

    /// <summary>
    /// Disposes the underlying runtime's semaphore.
    /// </summary>
    public void Dispose() => _core.Dispose();
}
