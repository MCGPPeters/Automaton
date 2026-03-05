// =============================================================================
// Program — The MVU Application Interface
// =============================================================================
// A Program is an Automaton specialized for Model-View-Update applications.
// It extends the kernel's transition function with two MVU-specific capabilities:
//
//     View          : Model → Document     (render the current state)
//     Subscriptions : Model → Subscription (declare external event sources)
//
// The type mapping from Automaton to MVU:
//
//     Automaton<TState,  TEvent,  TEffect,  TParameters>
//           ≡   <TModel, Message, Command,  TArgument>
//
// This interface is the compile-time contract that application authors implement.
// The runtime (AbiesRuntime) executes the MVU loop using the Automaton kernel's
// AutomatonRuntime with an Observer that renders views and a platform-supplied
// Interpreter that executes commands.
//
// Navigation (URL changes, link clicks) is modeled as regular Messages rather
// than separate interface members. This follows the Open/Closed Principle:
// the Program interface stays lean, and applications opt into navigation by
// handling UrlChanged / LinkClicked messages in their Update function.
//
// See also:
//   - Elm's Platform.Program: https://package.elm-lang.org/packages/elm/core/latest/Platform
//   - Automaton kernel: Automaton<TState, TEvent, TEffect, TParameters>
// =============================================================================

using Abies.DOM;
using Abies.Subscriptions;

namespace Abies;

/// <summary>
/// An MVU application: an <see cref="Automaton.Automaton{TState,TEvent,TEffect,TParameters}"/>
/// extended with <see cref="View"/> and <see cref="Subscriptions"/> capabilities.
/// </summary>
/// <remarks>
/// <para>
/// Implement this interface to define a complete MVU application. The runtime
/// calls <c>Initialize</c> and <c>Transition</c> from the
/// kernel, plus <see cref="View"/> and <see cref="Subscriptions"/> after each
/// state change.
/// </para>
/// <para>
/// The type parameters map from the Automaton kernel to MVU terminology:
/// <list type="table">
///   <listheader><term>Automaton</term><description>MVU</description></listheader>
///   <item><term>TState</term><description>TModel — the application model</description></item>
///   <item><term>TEvent</term><description><see cref="Message"/> — user/system events</description></item>
///   <item><term>TEffect</term><description><see cref="Command"/> — side effects</description></item>
///   <item><term>TParameters</term><description>TArgument — initialization flags</description></item>
/// </list>
/// </para>
/// <example>
/// <code>
/// using Abies;
/// using Abies.DOM;
/// using Abies.Subscriptions;
/// using Automaton;
/// using static Abies.Html.Elements;
/// using static Abies.Html.Attributes;
/// using static Abies.Html.Events;
///
/// public record CounterModel(int Count);
///
/// public interface CounterMessage : Message
/// {
///     record struct Increment : CounterMessage;
///     record struct Decrement : CounterMessage;
/// }
///
/// public class Counter : Program&lt;CounterModel, Unit&gt;
/// {
///     public static (CounterModel, Command) Initialize(Unit _) =&gt;
///         (new CounterModel(0), Commands.None);
///
///     public static (CounterModel, Command) Transition(CounterModel model, Message message) =&gt;
///         message switch
///         {
///             CounterMessage.Increment =&gt; (model with { Count = model.Count + 1 }, Commands.None),
///             CounterMessage.Decrement =&gt; (model with { Count = model.Count - 1 }, Commands.None),
///             _ =&gt; (model, Commands.None)
///         };
///
///     public static Document View(CounterModel model) =&gt;
///         new("Counter",
///             div([], [
///                 button([onclick(() =&gt; new CounterMessage.Decrement())], [text("-")]),
///                 text(model.Count.ToString()),
///                 button([onclick(() =&gt; new CounterMessage.Increment())], [text("+")])
///             ]));
///
///     public static Subscription Subscriptions(CounterModel model) =&gt;
///         SubscriptionModule.None;
/// }
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TModel">The application model (state).</typeparam>
/// <typeparam name="TArgument">Initialization parameters. Use <see cref="Automaton.Unit"/> for parameterless apps.</typeparam>
public interface Program<TModel, TArgument> : Automaton.Automaton<TModel, Message, Command, TArgument>
{
    /// <summary>
    /// Renders the current model as a virtual DOM <see cref="Document"/>.
    /// Called after every state transition to produce the new view.
    /// </summary>
    /// <remarks>
    /// This function must be <b>pure</b> — no side effects, no mutation.
    /// The runtime diffs the returned document against the previous one
    /// and applies the minimal set of patches to the actual DOM.
    /// </remarks>
    /// <param name="model">The current application model.</param>
    /// <returns>A virtual DOM document describing the desired UI.</returns>
    static abstract Document View(TModel model);

    /// <summary>
    /// Declares the external event sources the application wants to listen to.
    /// Called after every state transition to allow subscriptions to change
    /// based on the current model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Subscriptions are <b>declarative</b> — the application describes <em>what</em>
    /// it wants to subscribe to, and the runtime's <see cref="SubscriptionManager"/>
    /// handles the lifecycle (starting new subscriptions, keeping unchanged ones,
    /// stopping removed ones).
    /// </para>
    /// <para>
    /// Return <see cref="SubscriptionModule.None"/> if the application has no
    /// subscriptions in its current state.
    /// </para>
    /// </remarks>
    /// <param name="model">The current application model.</param>
    /// <returns>The desired set of subscriptions for the current state.</returns>
    static abstract Subscription Subscriptions(TModel model);
}

// =============================================================================
// Navigation Types — URL Changes as Regular Messages
// =============================================================================
// Navigation is not baked into the Program interface. Instead, URL changes
// and link clicks are modeled as regular Messages that applications handle
// in their Update (Transition) function. This keeps the Program interface
// lean and makes navigation opt-in.
//
// The browser runtime dispatches these messages when the URL changes or
// a link is clicked. Server-side rendering can inject the initial URL
// as an UrlChanged message during initialization.
//
// This design follows the Open/Closed Principle: the Program interface
// is closed for modification but open for extension via new message types.
// =============================================================================

/// <summary>
/// Represents a parsed URL with path and query components.
/// </summary>
/// <remarks>
/// <para>
/// This is a simplified URL representation for MVU navigation, inspired
/// by Elm's <c>Url</c> type. It carries the parts of a URL that are
/// relevant to client-side routing.
/// </para>
/// <para>
/// For full URL manipulation, convert to/from <see cref="System.Uri"/>
/// using the provided conversion methods.
/// </para>
/// </remarks>
/// <param name="Path">The path segments (e.g., <c>["articles", "my-slug"]</c>).</param>
/// <param name="Query">The query parameters as key-value pairs.</param>
/// <param name="Fragment">The URL fragment (hash), if present.</param>
public record Url(
    IReadOnlyList<string> Path,
    IReadOnlyDictionary<string, string> Query,
    Option<string> Fragment)
{
    /// <summary>
    /// An empty URL representing the root path with no query or fragment.
    /// </summary>
    public static readonly Url Root = new(
        Array.Empty<string>(),
        new Dictionary<string, string>(),
        Option<string>.None);

    /// <summary>
    /// Creates a <see cref="Url"/> from a <see cref="System.Uri"/>.
    /// </summary>
    /// <param name="uri">The URI to parse.</param>
    /// <returns>A parsed <see cref="Url"/> with path segments, query parameters, and fragment.</returns>
    public static Url FromUri(Uri uri)
    {
        var path = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        var query = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var queryString = uri.Query.TrimStart('?');
            foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                query[key] = value;
            }
        }

        var fragment = string.IsNullOrEmpty(uri.Fragment)
            ? Option<string>.None
            : Option.Some(uri.Fragment.TrimStart('#'));

        return new Url(path, query, fragment);
    }

    /// <summary>
    /// Converts this <see cref="Url"/> back to a relative URI string.
    /// </summary>
    public string ToRelativeUri()
    {
        var pathPart = Path.Count > 0 ? "/" + string.Join("/", Path) : "/";
        var queryPart = Query.Count > 0
            ? "?" + string.Join("&", Query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))
            : string.Empty;
        var fragmentPart = Fragment.Match(f => "#" + f, () => string.Empty);
        return $"{pathPart}{queryPart}{fragmentPart}";
    }
}

/// <summary>
/// A request to navigate to a URL. Dispatched by the browser runtime
/// when a link is clicked. The application decides how to handle it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Internal"/> links are handled by the application's router —
/// the runtime updates the browser URL without a full page reload.
/// </para>
/// <para>
/// <see cref="External"/> links cause a full page navigation to the
/// external URL. The application can intercept and modify this behavior
/// in its Update function.
/// </para>
/// </remarks>
public interface UrlRequest : Message
{
    /// <summary>
    /// A link within the application. The <see cref="Url"/> is already parsed.
    /// </summary>
    /// <param name="Url">The parsed internal URL.</param>
    record Internal(Url Url) : UrlRequest;

    /// <summary>
    /// A link to an external site. Contains the raw URL string.
    /// </summary>
    /// <param name="Href">The raw external URL (e.g., "https://example.com").</param>
    record External(string Href) : UrlRequest;
}

/// <summary>
/// Dispatched by the browser runtime when the URL changes
/// (e.g., browser back/forward, <c>pushState</c>, or initial page load).
/// </summary>
/// <param name="Url">The new URL.</param>
public record UrlChanged(Url Url) : Message;
