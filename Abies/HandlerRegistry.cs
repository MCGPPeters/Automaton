// =============================================================================
// HandlerRegistry — Event Handler Registration and Dispatch
// =============================================================================
// Maps commandIds to event handlers for the event delegation system.
// When the diff algorithm produces patches, event handlers are registered
// here with their commandId as the key. When the browser's event delegation
// system dispatches an event, the commandId is used to look up the handler
// and create the appropriate Message.
//
// This class lives in the core Abies project (not Abies.Browser) because
// the Runtime's observer needs it during UpdateHandlerRegistry.
//
// See also:
//   - Runtime.cs — registers/unregisters handlers during patch application
//   - Abies.Browser/Interop.cs — DispatchDomEvent calls CreateMessage
// =============================================================================

using System.Text.Json;
using Abies.DOM;

namespace Abies;

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
