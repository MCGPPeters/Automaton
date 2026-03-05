// =============================================================================
// Subscription — External Event Sources
// =============================================================================
// Subscriptions connect the MVU loop to the outside world: timers, WebSocket
// connections, browser events, server-sent events, etc.
//
// The view function only describes what the UI looks like. Subscriptions
// describe what external event sources the application is listening to.
//
// Each render cycle, the runtime calls Subscriptions(model) to get the
// desired set of subscriptions. The SubscriptionManager diffs the previous
// set against the new set and starts/stops subscription tasks accordingly.
//
// The type hierarchy:
//   Subscription
//   ├── None                              — no subscriptions
//   ├── Batch(IReadOnlyList<Subscription>) — multiple subscriptions
//   └── Source(SubscriptionKey, Start)     — a single subscription source
//
// This design is directly inspired by Elm's Sub type:
//   https://package.elm-lang.org/packages/elm/core/latest/Platform-Sub
//
// Subscriptions form a monoid:
//   Identity: None
//   Binary:   Batch
// =============================================================================

namespace Abies.Subscriptions;

/// <summary>
/// A function that dispatches a message into the MVU loop.
/// Used by subscription sources to feed external events into the application.
/// </summary>
/// <param name="message">The message to dispatch.</param>
public delegate void Dispatch(Message message);

/// <summary>
/// A function that starts a subscription source. The subscription should
/// run until the <paramref name="cancellationToken"/> is cancelled, dispatching
/// messages via <paramref name="dispatch"/> as external events arrive.
/// </summary>
/// <param name="dispatch">Function to dispatch messages into the MVU loop.</param>
/// <param name="cancellationToken">Token that signals when the subscription should stop.</param>
/// <returns>A task that completes when the subscription has fully stopped.</returns>
public delegate Task StartSubscription(Dispatch dispatch, CancellationToken cancellationToken);

/// <summary>
/// A unique key identifying a subscription source. Used by the
/// <see cref="SubscriptionManager"/> to determine which subscriptions
/// to start, keep, or stop when the model changes.
/// </summary>
/// <param name="Value">The unique key value.</param>
public readonly record struct SubscriptionKey(string Value);

/// <summary>
/// Describes the external event sources the application wants to listen to.
/// </summary>
/// <remarks>
/// <para>
/// Subscriptions are returned by the application's <c>Subscriptions(model)</c> function
/// each render cycle. The runtime diffs the old and new subscription sets to
/// determine which sources to start, keep, or stop.
/// </para>
/// <para>
/// Subscriptions form a <b>monoid</b>:
/// <list type="bullet">
///   <item><b>Identity</b>: <see cref="None"/> — no subscriptions.</item>
///   <item><b>Binary operation</b>: <see cref="Batch"/> — combines subscriptions.</item>
/// </list>
/// </para>
/// </remarks>
public abstract record Subscription
{
    /// <summary>Prevents external inheritance.</summary>
    private Subscription() { }

    /// <summary>
    /// No subscriptions. The identity element of the subscription monoid.
    /// </summary>
    public sealed record None : Subscription;

    /// <summary>
    /// A batch of subscriptions to manage together. The binary operation of the subscription monoid.
    /// </summary>
    /// <param name="Subscriptions">The subscriptions in this batch.</param>
    public sealed record Batch(IReadOnlyList<Subscription> Subscriptions) : Subscription;

    /// <summary>
    /// A single subscription source identified by a unique key.
    /// </summary>
    /// <remarks>
    /// The <see cref="Key"/> is used for identity — when the runtime sees
    /// the same key in consecutive render cycles, it keeps the existing
    /// running subscription instead of stopping and restarting it.
    /// </remarks>
    /// <param name="Key">Unique identifier for this subscription source.</param>
    /// <param name="Start">The function that starts the subscription.</param>
    public sealed record Source(SubscriptionKey Key, StartSubscription Start) : Subscription;
}

/// <summary>
/// Factory methods for creating <see cref="Subscription"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Provides ergonomic constructors for subscriptions, including common patterns
/// like periodic timers via <see cref="Every(TimeSpan, Func{Message})"/>.
/// </para>
/// <example>
/// <code>
/// // No subscriptions
/// static Subscription Subscriptions(Model model) =>
///     SubscriptionModule.None;
///
/// // A timer that sends a Tick message every second
/// static Subscription Subscriptions(Model model) =>
///     SubscriptionModule.Every(TimeSpan.FromSeconds(1), () =&gt; new Tick());
///
/// // Multiple subscriptions
/// static Subscription Subscriptions(Model model) =>
///     SubscriptionModule.Batch(
///         SubscriptionModule.Every(TimeSpan.FromSeconds(1), () =&gt; new Tick()),
///         SubscriptionModule.Create("websocket", (dispatch, ct) =&gt; ConnectWebSocket(dispatch, ct)));
/// </code>
/// </example>
/// </remarks>
public static class SubscriptionModule
{
    /// <summary>
    /// No subscriptions. Singleton instance.
    /// </summary>
    public static readonly Subscription None = new Subscription.None();

    /// <summary>
    /// Combines multiple subscriptions into a single batch.
    /// </summary>
    /// <param name="subscriptions">The subscriptions to batch together.</param>
    /// <returns>
    /// A <see cref="Subscription.Batch"/> if there are multiple subscriptions,
    /// the single subscription if there is exactly one,
    /// or <see cref="None"/> if the collection is empty.
    /// </returns>
    public static Subscription Batch(params Subscription[] subscriptions) =>
        subscriptions.Length switch
        {
            0 => None,
            1 => subscriptions[0],
            _ => new Subscription.Batch(subscriptions)
        };

    /// <summary>
    /// Creates a named subscription source with a custom start function.
    /// </summary>
    /// <param name="key">The unique key identifying this subscription.</param>
    /// <param name="start">The function that starts the subscription.</param>
    public static Subscription Create(string key, StartSubscription start) =>
        new Subscription.Source(new SubscriptionKey(key), start);

    /// <summary>
    /// Creates a periodic timer subscription that dispatches a message at fixed intervals.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="PeriodicTimer"/> for efficient, low-allocation periodic scheduling.
    /// The timer runs until the cancellation token is triggered by the subscription manager.
    /// </remarks>
    /// <param name="interval">The interval between ticks.</param>
    /// <param name="message">Factory that creates the message to dispatch on each tick.</param>
    /// <returns>A subscription source keyed by <c>"every:{interval.TotalMilliseconds}"</c>.</returns>
    public static Subscription Every(TimeSpan interval, Func<Message> message) =>
        new Subscription.Source(
            new SubscriptionKey($"every:{interval.TotalMilliseconds}"),
            async (dispatch, cancellationToken) =>
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    dispatch(message());
                }
            });

    /// <summary>
    /// Creates a periodic timer subscription with a custom key for disambiguation.
    /// </summary>
    /// <remarks>
    /// Use this overload when you have multiple timers with the same interval
    /// but different purposes (e.g., a polling timer and an animation timer).
    /// </remarks>
    /// <param name="key">The unique key identifying this timer subscription.</param>
    /// <param name="interval">The interval between ticks.</param>
    /// <param name="message">Factory that creates the message to dispatch on each tick.</param>
    public static Subscription Every(string key, TimeSpan interval, Func<Message> message) =>
        new Subscription.Source(
            new SubscriptionKey(key),
            async (dispatch, cancellationToken) =>
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    dispatch(message());
                }
            });
}
