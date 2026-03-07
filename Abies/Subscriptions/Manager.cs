// =============================================================================
// Subscription Manager — Lifecycle Management for Subscriptions
// =============================================================================
// The SubscriptionManager diffs the desired subscription set (returned by
// the application's Subscriptions function) against the currently running
// subscriptions, and starts/stops tasks accordingly.
//
// This is a differential reconciliation algorithm, structurally similar to
// the virtual DOM diff but applied to background task lifecycle:
//
//     Diff(oldSubs, newSubs) → { toStart, toStop, toKeep }
//
// Each running subscription is a Task with a CancellationTokenSource.
// Stopping a subscription cancels the token and awaits the task to
// ensure clean shutdown.
//
// The manager is stateless — it takes the current running state and returns
// the new running state. The runtime holds the state between render cycles.
//
// This design follows Elm's subscription manager:
//   https://github.com/elm/core/blob/master/src/Elm/Kernel/Platform.js
// =============================================================================

using System.Diagnostics;

namespace Abies.Subscriptions;

/// <summary>
/// The current state of all running subscriptions.
/// </summary>
/// <param name="Running">Map from subscription key to running subscription.</param>
public readonly record struct SubscriptionState(
    IReadOnlyDictionary<SubscriptionKey, RunningSubscription> Running)
{
    /// <summary>
    /// An empty subscription state with no running subscriptions.
    /// </summary>
    public static readonly SubscriptionState Empty =
        new(new Dictionary<SubscriptionKey, RunningSubscription>());
}

/// <summary>
/// A subscription source that is currently running as a background task.
/// </summary>
/// <param name="Key">The unique key identifying this subscription.</param>
/// <param name="CTS">The cancellation token source used to stop the subscription.</param>
/// <param name="Task">The running task that represents the subscription's lifetime.</param>
public sealed record RunningSubscription(SubscriptionKey Key, CancellationTokenSource CTS, Task Task);

/// <summary>
/// Manages subscription lifecycle by diffing desired subscriptions against running ones.
/// </summary>
/// <remarks>
/// <para>
/// The manager is a pure function over <see cref="SubscriptionState"/>:
/// <code>
/// Update(state, desired, dispatch) → newState
/// </code>
/// It determines which subscriptions to start, keep, or stop based on
/// key-based identity comparison between the current and desired sets.
/// </para>
/// <para>
/// <b>Design principle:</b> This is differential reconciliation applied to
/// background tasks, the same idea as virtual DOM diffing applied to UI nodes.
/// The key insight from Elm is that subscriptions are declarative — the
/// application says <em>what</em> it wants to subscribe to, and the manager
/// figures out the minimal set of start/stop operations.
/// </para>
/// </remarks>
internal static class SubscriptionManager
{
    private static readonly ActivitySource _activitySource = new("Abies.Subscriptions");

    /// <summary>
    /// Starts the subscription manager with an initial subscription set.
    /// </summary>
    /// <param name="subscription">The initial desired subscriptions.</param>
    /// <param name="dispatch">The dispatch function for feeding messages into the MVU loop.</param>
    /// <returns>The initial subscription state with all sources started.</returns>
    internal static SubscriptionState Start(Subscription subscription, Dispatch dispatch) =>
        Update(SubscriptionState.Empty, subscription, dispatch);

    /// <summary>
    /// Updates the subscription state by diffing desired subscriptions against running ones.
    /// Starts new subscriptions, keeps matching ones, and stops removed ones.
    /// </summary>
    /// <param name="current">The current running subscription state.</param>
    /// <param name="desired">The desired subscription set from the application.</param>
    /// <param name="dispatch">The dispatch function for new subscriptions.</param>
    /// <returns>The updated subscription state.</returns>
    internal static SubscriptionState Update(
        SubscriptionState current,
        Subscription desired,
        Dispatch dispatch)
    {
        using var activity = _activitySource.StartActivity("Subscriptions.Update");

        var desiredSources = Flatten(desired);
        var newRunning = new Dictionary<SubscriptionKey, RunningSubscription>();

        // Start new subscriptions, keep existing ones
        foreach (var source in desiredSources)
        {
            if (current.Running.TryGetValue(source.Key, out var existing))
            {
                // Subscription still desired — keep running
                newRunning[source.Key] = existing;
            }
            else
            {
                // New subscription — start it
                var running = StartSubscription(source, dispatch);
                newRunning[source.Key] = running;
            }
        }

        // Stop subscriptions that are no longer desired
        foreach (var (key, running) in current.Running)
        {
            if (!newRunning.ContainsKey(key))
            {
                StopSubscription(running);
            }
        }

        activity?.SetTag("subscriptions.started", newRunning.Count - current.Running.Count);
        activity?.SetTag("subscriptions.total", newRunning.Count);

        return new SubscriptionState(newRunning);
    }

    /// <summary>
    /// Stops all running subscriptions. Called during application shutdown.
    /// </summary>
    /// <param name="state">The current subscription state.</param>
    internal static void Stop(SubscriptionState state)
    {
        using var activity = _activitySource.StartActivity("Subscriptions.Stop");

        foreach (var (_, running) in state.Running)
        {
            StopSubscription(running);
        }

        activity?.SetTag("subscriptions.stopped", state.Running.Count);
    }

    /// <summary>
    /// Flattens a subscription tree into a list of leaf <see cref="Subscription.Source"/> nodes.
    /// </summary>
    /// <param name="subscription">The subscription tree to flatten.</param>
    /// <returns>All source subscriptions in the tree.</returns>
    internal static IReadOnlyList<Subscription.Source> Flatten(Subscription subscription)
    {
        var sources = new List<Subscription.Source>();
        FlattenInto(subscription, sources);
        return sources;
    }

    private static void FlattenInto(Subscription subscription, List<Subscription.Source> sources)
    {
        switch (subscription)
        {
            case Subscription.None:
                break;

            case Subscription.Source source:
                sources.Add(source);
                break;

            case Subscription.Batch batch:
                foreach (var sub in batch.Subscriptions)
                {
                    FlattenInto(sub, sources);
                }
                break;
        }
    }

    /// <summary>
    /// Starts a single subscription source as a background task.
    /// </summary>
    private static RunningSubscription StartSubscription(Subscription.Source source, Dispatch dispatch)
    {
        using var activity = _activitySource.StartActivity("Subscription.Start");
        activity?.SetTag("subscription.key", source.Key.Value);

        var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            try
            {
                await source.Start(dispatch, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when subscription is stopped — swallow gracefully
            }
        });

        return new RunningSubscription(source.Key, cts, task);
    }

    /// <summary>
    /// Stops a running subscription by cancelling its token.
    /// </summary>
    /// <remarks>
    /// The cancellation is fire-and-forget — the task will complete asynchronously.
    /// The subscription's <see cref="StartSubscription"/> function is expected to
    /// observe the cancellation token and exit cleanly.
    /// </remarks>
    private static void StopSubscription(RunningSubscription running)
    {
        using var activity = _activitySource.StartActivity("Subscription.Stop");
        activity?.SetTag("subscription.key", running.Key.Value);

        running.CTS.Cancel();
        running.CTS.Dispose();
    }
}
