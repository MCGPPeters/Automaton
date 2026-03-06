// =============================================================================
// Navigation — Client-Side Routing Commands and Subscriptions
// =============================================================================
// Navigation connects the browser's history API to the MVU loop via two
// channels:
//
//   Commands (App → Browser):
//     PushUrl      — history.pushState  (adds to history stack)
//     ReplaceUrl   — history.replaceState (replaces current entry)
//     Back         — history.back()
//     Forward      — history.forward()
//     ExternalUrl  — full page navigation (window.location.href)
//
//   Subscriptions (Browser → App):
//     UrlChanges   — popstate events + link click interception
//
// This follows Elm's Browser.Navigation module:
//   https://package.elm-lang.org/packages/elm/browser/latest/Browser.Navigation
//
// Navigation commands are plain records implementing the Command interface.
// They are handled by the runtime's built-in navigation interpreter, which
// calls the appropriate JS interop methods. Application authors never need
// to handle these commands manually.
//
// The UrlChanges subscription listens for two kinds of browser events:
//   1. popstate — browser back/forward button, programmatic pushState/replaceState
//   2. Internal link clicks — <a> elements whose href is same-origin
//
// External links (cross-origin or target="_blank") are not intercepted —
// they follow normal browser behavior.
//
// See also:
//   - Program.cs — Url, UrlRequest, UrlChanged types
//   - Interop.cs — JSImport/JSExport bridge
//   - abies.js — browser-side navigation wiring
//   - Runtime.cs — navigation command interpretation
// =============================================================================

using Abies.Subscriptions;

namespace Abies;

/// <summary>
/// Navigation commands for controlling the browser's history and URL.
/// </summary>
/// <remarks>
/// <para>
/// Navigation commands are returned from the application's Update function
/// alongside the new model. The runtime interprets them by calling the
/// appropriate JS interop methods.
/// </para>
/// <example>
/// <code>
/// // In your Update function:
/// static (Model, Command) Transition(Model model, Message message) =>
///     message switch
///     {
///         NavigateToArticle(var slug) =>
///             (model, Navigation.PushUrl(new Url(["articles", slug], new(), Option&lt;string&gt;.None))),
///
///         GoBack =>
///             (model, Navigation.Back),
///
///         _ => (model, Commands.None)
///     };
/// </code>
/// </example>
/// </remarks>
public static class Navigation
{
    // =========================================================================
    // Commands (App → Browser)
    // =========================================================================

    /// <summary>
    /// Creates a command that pushes a new URL onto the browser's history stack.
    /// This adds a new entry — the user can navigate back to the previous URL.
    /// </summary>
    /// <param name="url">The URL to navigate to.</param>
    /// <returns>A command that the runtime interprets via <c>history.pushState</c>.</returns>
    public static Command PushUrl(Url url) => new NavigationCommand.Push(url);

    /// <summary>
    /// Creates a command that replaces the current URL in the browser's history.
    /// This does NOT add a new history entry — the previous URL is lost.
    /// </summary>
    /// <param name="url">The URL to replace the current entry with.</param>
    /// <returns>A command that the runtime interprets via <c>history.replaceState</c>.</returns>
    public static Command ReplaceUrl(Url url) => new NavigationCommand.Replace(url);

    /// <summary>
    /// A command that navigates the browser back one step in history.
    /// Equivalent to the user pressing the back button.
    /// </summary>
    public static readonly Command Back = new NavigationCommand.GoBack();

    /// <summary>
    /// A command that navigates the browser forward one step in history.
    /// Equivalent to the user pressing the forward button.
    /// </summary>
    public static readonly Command Forward = new NavigationCommand.GoForward();

    /// <summary>
    /// Creates a command that navigates to an external URL.
    /// This triggers a full page reload — the WASM application is unloaded.
    /// </summary>
    /// <param name="href">The external URL to navigate to (e.g., "https://example.com").</param>
    /// <returns>A command that the runtime interprets via <c>window.location.href</c>.</returns>
    public static Command ExternalUrl(string href) => new NavigationCommand.External(href);

    // =========================================================================
    // Subscription (Browser → App)
    // =========================================================================

    /// <summary>
    /// Creates a subscription that listens for URL changes in the browser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This subscription captures two kinds of events:
    /// <list type="bullet">
    ///   <item><b>popstate</b> — browser back/forward, programmatic history changes</item>
    ///   <item><b>Internal link clicks</b> — same-origin <c>&lt;a&gt;</c> elements are
    ///     intercepted, the URL is pushed via <c>history.pushState</c>, and a
    ///     <see cref="UrlChanged"/> message is dispatched</item>
    /// </list>
    /// </para>
    /// <para>
    /// The <paramref name="toMessage"/> function converts the new <see cref="Url"/>
    /// into an application-specific message. A common pattern is to wrap it in
    /// a <see cref="UrlChanged"/> message:
    /// </para>
    /// <example>
    /// <code>
    /// static Subscription Subscriptions(Model model) =>
    ///     Navigation.UrlChanges(url => new UrlChanged(url));
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="toMessage">
    /// Converts a parsed <see cref="Url"/> into a <see cref="Message"/> for the MVU loop.
    /// </param>
    /// <returns>A subscription source keyed by <c>"navigation:urlChanges"</c>.</returns>
    public static Subscription UrlChanges(Func<Url, Message> toMessage) =>
        SubscriptionModule.Create("navigation:urlChanges", (dispatch, cancellationToken) =>
        {
            // Store the callback so NavigationCallbacks can dispatch URL changes
            NavigationCallbacks.OnUrlChange = url => dispatch(toMessage(url));

            // Keep the subscription alive until cancelled
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() =>
            {
                NavigationCallbacks.OnUrlChange = null;
                tcs.TrySetResult();
            });
            return tcs.Task;
        });

    // =========================================================================
    // Url Helpers
    // =========================================================================

    /// <summary>
    /// Returns the current browser URL as a <see cref="Url"/> by parsing
    /// the given URI string (typically <c>window.location</c>).
    /// </summary>
    /// <param name="locationHref">The full browser location href string.</param>
    /// <returns>A parsed <see cref="Url"/>.</returns>
    public static Url ParseUrl(string locationHref)
    {
        var uri = new Uri(locationHref, UriKind.RelativeOrAbsolute);
        if (!uri.IsAbsoluteUri)
            uri = new Uri(new Uri("https://localhost"), locationHref);
        return Url.FromUri(uri);
    }
}

// =============================================================================
// Navigation Command Types
// =============================================================================
// These are the concrete command records that the runtime's navigation
// interpreter pattern-matches against. They are internal implementation
// details — application code uses the Navigation static methods above.
// =============================================================================

/// <summary>
/// Navigation command types — handled by the runtime's built-in interpreter.
/// </summary>
/// <remarks>
/// <para>
/// These commands are distinguished from application-defined commands via
/// pattern matching in the runtime. The runtime handles them first; if a
/// command is not a navigation command, it falls through to the caller-supplied
/// interpreter.
/// </para>
/// </remarks>
public interface NavigationCommand : Command
{
    /// <summary>Push a URL onto the history stack.</summary>
    /// <param name="Url">The URL to push.</param>
    sealed record Push(Url Url) : NavigationCommand;

    /// <summary>Replace the current history entry.</summary>
    /// <param name="Url">The URL to replace with.</param>
    sealed record Replace(Url Url) : NavigationCommand;

    /// <summary>Navigate back one history entry.</summary>
    sealed record GoBack : NavigationCommand;

    /// <summary>Navigate forward one history entry.</summary>
    sealed record GoForward : NavigationCommand;

    /// <summary>Navigate to an external URL (full page load).</summary>
    /// <param name="Href">The external URL.</param>
    sealed record External(string Href) : NavigationCommand;
}

// =============================================================================
// Navigation Callbacks — Bridge between JS and Subscription
// =============================================================================
// The JS side calls OnUrlChanged (via JSExport) when a popstate event fires
// or an internal link is clicked. This static callback dispatches the URL
// change into the subscription's dispatch function.
//
// This is intentionally a static mutable field — WASM is single-threaded
// and there is exactly one navigation subscription per application.
// =============================================================================

/// <summary>
/// Static callback bridge between JavaScript navigation events and the
/// <see cref="Navigation.UrlChanges"/> subscription.
/// </summary>
internal static class NavigationCallbacks
{
    /// <summary>
    /// Called by <see cref="Interop.OnUrlChanged"/> when the browser URL changes.
    /// Set by <see cref="Navigation.UrlChanges"/> when the subscription starts.
    /// </summary>
    internal static Action<Url>? OnUrlChange { get; set; }

    /// <summary>
    /// Processes a URL change notification from JavaScript.
    /// Parses the URL string into a <see cref="Url"/> and dispatches it.
    /// </summary>
    /// <param name="urlString">The new URL as a string from the browser.</param>
    internal static void HandleUrlChanged(string urlString)
    {
        if (OnUrlChange is null)
            return;

        var uri = new Uri(urlString, UriKind.RelativeOrAbsolute);

        // For relative URLs, construct a proper URI for parsing
        if (!uri.IsAbsoluteUri)
        {
            uri = new Uri(new Uri("https://localhost"), urlString);
        }

        var url = Url.FromUri(uri);
        OnUrlChange(url);
    }
}
