// =============================================================================
// Event Data Deserializers — Trim-Safe JSON Deserialization Lookup
// =============================================================================
// Maps event data types to source-generated deserializer functions.
//
// The event data types form a closed set (InputEventData, KeyEventData,
// PointerEventData, ScrollEventData, GenericEventData). This class provides
// a type-safe lookup from typeof(T) to a Func<string, object?> that uses
// the source-generated AbiesEventJsonContext — no reflection needed.
//
// Architecture note:
//   The Handler record carries a Func<string, object?> Deserializer instead
//   of a Type. This inverts the dependency: instead of HandlerRegistry
//   knowing how to deserialize (which required reflection), the handler
//   itself carries the trim-safe deserializer created at registration time.
//
// See also:
//   - AbiesEventJsonContext.cs — the source-generated JSON context
//   - Events.cs — on<T>() calls Get<T>() to bind the deserializer
//   - HandlerRegistry.cs — calls handler.Deserializer(eventData)
// =============================================================================

using System.Collections.Frozen;
using System.Text.Json;

namespace Abies.Html;

/// <summary>
/// Provides trim-safe deserializer functions for event data types.
/// </summary>
/// <remarks>
/// Uses a frozen dictionary for O(1) lookup at runtime.
/// All deserializers use the source-generated <see cref="AbiesEventJsonContext"/>.
/// </remarks>
internal static class EventDataDeserializers
{
    private static readonly FrozenDictionary<Type, Func<string, object?>> Deserializers =
        new Dictionary<Type, Func<string, object?>>
        {
            [typeof(InputEventData)] = json =>
                JsonSerializer.Deserialize(json, AbiesEventJsonContext.Default.InputEventData),
            [typeof(KeyEventData)] = json =>
                JsonSerializer.Deserialize(json, AbiesEventJsonContext.Default.KeyEventData),
            [typeof(PointerEventData)] = json =>
                JsonSerializer.Deserialize(json, AbiesEventJsonContext.Default.PointerEventData),
            [typeof(ScrollEventData)] = json =>
                JsonSerializer.Deserialize(json, AbiesEventJsonContext.Default.ScrollEventData),
            [typeof(GenericEventData)] = json =>
                JsonSerializer.Deserialize(json, AbiesEventJsonContext.Default.GenericEventData),
        }.ToFrozenDictionary();

    /// <summary>
    /// Gets the source-generated deserializer for event data type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The event data type.</typeparam>
    /// <returns>A trim-safe deserializer function.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <typeparamref name="T"/> is not a recognized event data type.
    /// </exception>
    public static Func<string, object?> Get<T>() =>
        Deserializers.TryGetValue(typeof(T), out var deserializer)
            ? deserializer
            : throw new InvalidOperationException(
                $"No source-generated deserializer registered for event data type '{typeof(T).Name}'. " +
                $"Register it in {nameof(AbiesEventJsonContext)} and {nameof(EventDataDeserializers)}.");
}
