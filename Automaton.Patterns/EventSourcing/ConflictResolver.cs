// =============================================================================
// ConflictResolver — Domain-Aware Optimistic Concurrency Resolution
// =============================================================================
// Extends the Decider pattern with a conflict resolution function for
// event-sourced aggregates operating under optimistic concurrency control.
//
// When two processes concurrently append to the same stream, the second
// writer encounters a ConcurrencyException. A ConflictResolver examines
// "our" intended events against "their" committed events and decides
// whether they are compatible — enabling automatic retry without reloading
// the entire aggregate.
//
// Mathematically, ResolveConflicts is a total function returning a Result:
//
//     resolveConflicts : S × S × [Σ_ours] × [Σ_theirs] → Result<[Σ_resolved], ConflictNotResolved>
//
// Where ConflictNotResolved signals an irreconcilable conflict via the
// Result error channel — no exceptions for expected domain outcomes.
//
// Inspired by:
//     - Radix framework (MCGPPeters/Radix) — Aggregate.ResolveConflicts
//     - Kung & Robinson (1981) — Optimistic Concurrency Control
//     - CRDTs — Commutative operations that can be safely reordered
// =============================================================================

namespace Automaton.Patterns.EventSourcing;

/// <summary>
/// A <see cref="Decider{TState,TCommand,TEvent,TEffect,TError,TParameters}"/> that can resolve
/// optimistic concurrency conflicts in event-sourced aggregates.
/// </summary>
/// <remarks>
/// <para>
/// When an <see cref="AggregateRunner{TDecider,TState,TCommand,TEvent,TEffect,TError,TParameters}"/>
/// detects a concurrency conflict (another process appended events since we last read),
/// it throws <see cref="ConcurrencyException"/>. For Deciders that implement
/// <c>ConflictResolver</c>, the
/// <see cref="ResolvingAggregateRunner{TDecider,TState,TCommand,TEvent,TEffect,TError,TParameters}"/>
/// catches the conflict and calls <see cref="ResolveConflicts"/> to attempt automatic
/// reconciliation.
/// </para>
/// <para>
/// The resolution function receives:
/// <list type="bullet">
///     <item><description>
///         <c>currentState</c> — the merged state after replaying "their" events on top of
///         the pre-conflict state. This is the true current state in the store.
///     </description></item>
///     <item><description>
///         <c>projectedState</c> — the state that would result from also applying "our"
///         events on top of the merged state. The runner pre-computes this via
///         <c>Transition</c> so the domain never needs to call it.
///     </description></item>
///     <item><description>
///         <c>ourEvents</c> — the events we intended to append (from our <c>Decide</c> call)
///     </description></item>
///     <item><description>
///         <c>theirEvents</c> — the events committed by the other process since our last
///         known version
///     </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Commutativity insight:</b> Many domain operations are commutative — two independent
/// "Add 3" commands produce the same final state regardless of order. For these operations,
/// <c>ResolveConflicts</c> can simply re-validate and return the original events. For
/// non-commutative operations (e.g., "set to X" vs "set to Y"), the function returns
/// <see cref="ConflictNotResolved"/> via the <see cref="Result{TSuccess,TError}"/> error
/// channel to signal an irreconcilable conflict — no exceptions for expected outcomes.
/// </para>
/// <example>
/// <code>
/// // A counter with commutative increments:
/// public class MyDecider : ConflictResolver&lt;State, Cmd, Event, Effect, Error&gt;
/// {
///     // ... Init, Decide, Transition ...
///
///     public static Result&lt;Event[], ConflictNotResolved&gt; ResolveConflicts(
///         State currentState, State projectedState,
///         Event[] ourEvents, IReadOnlyList&lt;Event&gt; theirEvents)
///     {
///         // The runner already computed projectedState via Transition —
///         // just check invariants on the result
///         return projectedState.Count &gt; MaxCount
///             ? Result&lt;Event[], ConflictNotResolved&gt;.Err(
///                 new ConflictNotResolved("Would exceed max count"))
///             : Result&lt;Event[], ConflictNotResolved&gt;.Ok(ourEvents);
///     }
/// }
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TState">The aggregate state.</typeparam>
/// <typeparam name="TCommand">Commands representing user intent.</typeparam>
/// <typeparam name="TEvent">Events representing validated facts.</typeparam>
/// <typeparam name="TEffect">Effects produced by transitions.</typeparam>
/// <typeparam name="TError">Errors produced by invalid commands.</typeparam>
/// <typeparam name="TParameters">The type of parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Init"/>.</typeparam>
public interface ConflictResolver<TState, TCommand, TEvent, TEffect, TError, TParameters>
    : Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
{
    /// <summary>
    /// Attempts to resolve a concurrency conflict by reconciling our intended events
    /// with events committed by another process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This function MUST be pure and total: it always returns a
    /// <see cref="Result{TSuccess,TError}"/> — either resolved events on success,
    /// or a <see cref="ConflictNotResolved"/> reason on failure. No exceptions
    /// for expected domain outcomes.
    /// </para>
    /// <para>
    /// The runner pre-computes both <paramref name="currentState"/> and
    /// <paramref name="projectedState"/> via <c>Transition</c>, so implementations
    /// never need to call <c>Transition</c> themselves. Just inspect the states and
    /// event lists to determine compatibility.
    /// </para>
    /// <para>
    /// Return <c>Result.Ok(resolvedEvents)</c> to append the resolved events to the
    /// store at the new version. Return <c>Result.Err(new ConflictNotResolved(reason))</c>
    /// when the conflict is irreconcilable.
    /// </para>
    /// </remarks>
    /// <param name="currentState">
    /// The merged state: initial state folded with all committed events (including "theirs").
    /// This represents the true current state of the aggregate in the store.
    /// </param>
    /// <param name="projectedState">
    /// The state that would result from applying our events on top of the merged state.
    /// Pre-computed by the runner via <c>Transition</c> — use this to check invariants
    /// without calling <c>Transition</c> yourself.
    /// </param>
    /// <param name="ourEvents">
    /// The events our <c>Decide</c> call produced, which failed to append due to the conflict.
    /// </param>
    /// <param name="theirEvents">
    /// The events committed by another process since our last known version.
    /// </param>
    /// <returns>
    /// <c>Ok(resolvedEvents)</c> to append at the new version, or
    /// <c>Err(ConflictNotResolved)</c> when the conflict is irreconcilable.
    /// </returns>
    static abstract Result<TEvent[], ConflictNotResolved> ResolveConflicts(
        TState currentState,
        TState projectedState,
        TEvent[] ourEvents,
        IReadOnlyList<TEvent> theirEvents);
}

/// <summary>
/// Represents an irreconcilable concurrency conflict that domain logic cannot resolve.
/// </summary>
/// <remarks>
/// <para>
/// Used as the error channel in <see cref="Result{TSuccess,TError}"/> returned by
/// <see cref="ConflictResolver{TState,TCommand,TEvent,TEffect,TError,TParameters}.ResolveConflicts"/>.
/// Unlike exceptions, this is a value type that carries a reason string without
/// the overhead of stack trace capture.
/// </para>
/// <para>
/// Examples of irreconcilable conflicts:
/// <list type="bullet">
///     <item><description>Two "set to X" commands with different values</description></item>
///     <item><description>Our events would violate invariants given their events</description></item>
///     <item><description>Their events moved the aggregate to a terminal state</description></item>
/// </list>
/// </para>
/// </remarks>
/// <param name="Reason">A description of why the conflict could not be resolved.</param>
public readonly record struct ConflictNotResolved(string Reason);
