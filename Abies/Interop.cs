// =============================================================================
// Interop — JavaScript ↔ .NET Bridge for Browser WASM
// =============================================================================
// This static partial class provides the [JSImport]/[JSExport] declarations
// that connect the .NET Abies runtime to the browser's DOM via abies.js.
//
// Architecture:
//   .NET → JS (JSImport):
//     RenderInitial    — sets innerHTML for the first render
//     ApplyBinaryBatch — applies a binary-encoded batch of DOM patches
//     SetTitle         — sets document.title
//     NavigateTo       — history.pushState for client-side routing
//     SetupEventDelegation — registers event listeners at the root
//
//   JS → .NET (JSExport):
//     DispatchDomEvent — called by event delegation when a DOM event fires
//
// Binary Protocol:
//   Patches are serialized by RenderBatchWriter into a compact binary format:
//
//     Header (8 bytes):
//       PatchCount:       int32 (4 bytes)
//       StringTableOffset: int32 (4 bytes)
//
//     Patch Entries (16 bytes each):
//       Type:  int32 (4 bytes) — BinaryPatchType enum value
//       Field1: int32 (4 bytes) — string table index (-1 = null)
//       Field2: int32 (4 bytes) — string table index (-1 = null)
//       Field3: int32 (4 bytes) — string table index (-1 = null)
//
//     String Table:
//       LEB128 length prefix + UTF-8 bytes for each string
//       String deduplication via Dictionary lookup
//
//   Transfer uses JSType.MemoryView (Span<byte>) for zero-copy interop.
//   The JS side must call .slice() to get a stable Uint8Array before the
//   interop call returns (the Span is stack-allocated).
//
// Event Delegation:
//   A single listener per event type is registered at the document level.
//   When an event fires, the handler walks up from the target looking for
//   a data-event-{eventType} attribute. The attribute value is the commandId
//   which maps to a Handler in the HandlerRegistry.
//
// See also:
//   - RenderBatchWriter.cs — binary serialization
//   - abies.js — browser-side runtime
//   - Runtime.cs — AbiesRuntime wiring
// =============================================================================

using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Abies.DOM;

namespace Abies;

/// <summary>
/// JavaScript interop bridge for the Abies browser runtime.
/// </summary>
/// <remarks>
/// <para>
/// This class is split into two halves:
/// <list type="bullet">
///   <item><b>JSImport</b>: .NET calls into JavaScript (DOM mutations, navigation)</item>
///   <item><b>JSExport</b>: JavaScript calls into .NET (event dispatch)</item>
/// </list>
/// </para>
/// <para>
/// The module name <c>"Abies"</c> corresponds to the JS module loaded via
/// <c>JSHost.ImportAsync("Abies", "/abies.js")</c> at startup.
/// </para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("browser")]
public static partial class Interop
{
    // =========================================================================
    // .NET → JavaScript (JSImport)
    // =========================================================================

    /// <summary>
    /// Sets the innerHTML of the app root element for the initial render.
    /// </summary>
    /// <param name="rootId">The DOM element ID (e.g., "app").</param>
    /// <param name="html">The full HTML string produced by <see cref="Render.Html"/>.</param>
    [JSImport("renderInitial", "Abies")]
    internal static partial void RenderInitial(string rootId, string html);

    /// <summary>
    /// Applies a binary-encoded batch of DOM patches.
    /// </summary>
    /// <remarks>
    /// The binary data is produced by <see cref="RenderBatchWriter"/> and transferred
    /// via <see cref="JSType.MemoryView"/> for zero-copy interop. The JS side must
    /// call <c>.slice()</c> on the MemoryView to get a stable <c>Uint8Array</c>
    /// before the interop call returns.
    /// </remarks>
    /// <param name="batchData">The binary patch data as a Span&lt;byte&gt;.</param>
    [JSImport("applyBinaryBatch", "Abies")]
    internal static partial void ApplyBinaryBatch(
        [JSMarshalAs<JSType.MemoryView>] Span<byte> batchData);

    /// <summary>
    /// Sets the document title.
    /// </summary>
    /// <param name="title">The new page title.</param>
    [JSImport("setTitle", "Abies")]
    internal static partial void SetTitle(string title);

    /// <summary>
    /// Navigates to a URL via <c>history.pushState</c>.
    /// </summary>
    /// <param name="url">The target URL (relative or absolute).</param>
    [JSImport("navigateTo", "Abies")]
    internal static partial void NavigateTo(string url);

    /// <summary>
    /// Sets up event delegation on the document for all common event types.
    /// </summary>
    [JSImport("setupEventDelegation", "Abies")]
    internal static partial void SetupEventDelegation();

    // =========================================================================
    // Navigation (.NET → JavaScript)
    // =========================================================================

    /// <summary>
    /// Replaces the current URL in the browser history via <c>history.replaceState</c>.
    /// Unlike <see cref="NavigateTo"/>, this does not add a new history entry.
    /// </summary>
    /// <param name="url">The URL to replace the current entry with.</param>
    [JSImport("replaceUrl", "Abies")]
    internal static partial void ReplaceUrl(string url);

    /// <summary>
    /// Navigates the browser back one step in history via <c>history.back()</c>.
    /// </summary>
    [JSImport("historyBack", "Abies")]
    internal static partial void HistoryBack();

    /// <summary>
    /// Navigates the browser forward one step in history via <c>history.forward()</c>.
    /// </summary>
    [JSImport("historyForward", "Abies")]
    internal static partial void HistoryForward();

    /// <summary>
    /// Navigates to an external URL by setting <c>window.location.href</c>.
    /// This triggers a full page reload — the WASM application is unloaded.
    /// </summary>
    /// <param name="href">The external URL to navigate to.</param>
    [JSImport("externalNavigate", "Abies")]
    internal static partial void ExternalNavigate(string href);

    /// <summary>
    /// Sets up navigation interception: registers a <c>popstate</c> listener and
    /// intercepts internal <c>&lt;a&gt;</c> link clicks. Calls back to .NET via
    /// <see cref="OnUrlChanged"/> when the URL changes.
    /// </summary>
    [JSImport("setupNavigation", "Abies")]
    internal static partial void SetupNavigation();

    // =========================================================================
    // JavaScript → .NET (JSExport)
    // =========================================================================

    /// <summary>
    /// Called by abies.js when a DOM event fires on an element with a
    /// <c>data-event-{eventType}</c> attribute. The commandId maps to
    /// a <see cref="Handler"/> in the <see cref="HandlerRegistry"/>.
    /// </summary>
    /// <param name="commandId">The handler command ID from the data-event attribute.</param>
    /// <param name="eventName">The DOM event name (e.g., "click", "input").</param>
    /// <param name="eventData">Serialized event data (e.g., input value, key name).</param>
    [JSExport]
    public static void DispatchDomEvent(string commandId, string eventName, string eventData)
    {
        var message = HandlerRegistry.CreateMessage(commandId, eventData);
        if (message is not null)
        {
            HandlerRegistry.Dispatch?.Invoke(message);
        }
    }

    /// <summary>
    /// Called by abies.js when the browser URL changes (popstate event or
    /// intercepted link click). Delegates to <see cref="NavigationCallbacks"/>
    /// which routes the URL change to the navigation subscription.
    /// </summary>
    /// <param name="url">The new URL as a string from the browser (e.g., "/articles/my-slug").</param>
    [JSExport]
    public static void OnUrlChanged(string url) =>
        NavigationCallbacks.HandleUrlChanged(url);
}

/// <summary>
/// Registry mapping commandIds to event handlers.
/// </summary>
/// <remarks>
/// <para>
/// When the diff algorithm produces patches, event handlers are registered
/// in this registry with their commandId as the key. When the JS event
/// delegation system dispatches an event, the commandId is used to look up
/// the handler and create the appropriate <see cref="Message"/>.
/// </para>
/// <para>
/// The registry uses <see cref="Dictionary{TKey,TValue}"/> (not concurrent)
/// since WASM is single-threaded.
/// </para>
/// </remarks>
public static class HandlerRegistry
{
    private static readonly Dictionary<string, Handler> _handlers = new();

    /// <summary>
    /// The dispatch function for feeding messages into the MVU loop.
    /// Set by the browser runtime during startup.
    /// </summary>
    internal static Action<Message>? Dispatch { get; set; }

    /// <summary>
    /// Registers a handler by its commandId.
    /// </summary>
    /// <param name="handler">The handler to register.</param>
    public static void Register(Handler handler) =>
        _handlers[handler.CommandId] = handler;

    /// <summary>
    /// Unregisters a handler by its commandId.
    /// </summary>
    /// <param name="commandId">The commandId to remove.</param>
    public static void Unregister(string commandId) =>
        _handlers.Remove(commandId);

    /// <summary>
    /// Creates a message from a commandId and event data by looking up
    /// the registered handler.
    /// </summary>
    /// <param name="commandId">The handler commandId.</param>
    /// <param name="eventData">Raw event data string from JS.</param>
    /// <returns>The message to dispatch, or null if the handler was not found.</returns>
    public static Message? CreateMessage(string commandId, string eventData)
    {
        if (!_handlers.TryGetValue(commandId, out var handler))
            return null;

        // Static message handler — dispatch the pre-built message
        if (handler.Command is not null)
            return handler.Command;

        // Data-carrying handler — deserialize event data and call factory
        if (handler.WithData is not null && handler.DataType is not null)
        {
            var data = string.IsNullOrEmpty(eventData)
                ? null
                : JsonSerializer.Deserialize(eventData, handler.DataType);
            return handler.WithData(data);
        }

        return null;
    }

    /// <summary>
    /// Clears all registered handlers. Used during shutdown or testing.
    /// </summary>
    internal static void Clear() => _handlers.Clear();

    /// <summary>
    /// Registers all handlers from an element's attributes, and recursively
    /// from its children.
    /// </summary>
    /// <param name="node">The virtual DOM node to scan for handlers.</param>
    public static void RegisterHandlers(Node? node)
    {
        switch (node)
        {
            case Element element:
                foreach (var attr in element.Attributes)
                {
                    if (attr is Handler handler)
                    {
                        Register(handler);
                    }
                }

                foreach (var child in element.Children)
                {
                    RegisterHandlers(child);
                }

                break;

            case MemoNode memo:
                RegisterHandlers(memo.CachedNode);
                break;

            case LazyMemoNode lazy:
                RegisterHandlers(lazy.CachedNode ?? lazy.Evaluate());
                break;
        }
    }

    /// <summary>
    /// Unregisters all handlers from an element's attributes, and recursively
    /// from its children.
    /// </summary>
    /// <param name="node">The virtual DOM node to scan for handlers.</param>
    public static void UnregisterHandlers(Node? node)
    {
        switch (node)
        {
            case Element element:
                foreach (var attr in element.Attributes)
                {
                    if (attr is Handler handler)
                    {
                        Unregister(handler.CommandId);
                    }
                }

                foreach (var child in element.Children)
                {
                    UnregisterHandlers(child);
                }

                break;

            case MemoNode memo:
                UnregisterHandlers(memo.CachedNode);
                break;

            case LazyMemoNode lazy:
                UnregisterHandlers(lazy.CachedNode);
                break;
        }
    }
}
