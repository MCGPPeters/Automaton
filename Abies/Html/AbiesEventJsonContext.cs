// =============================================================================
// Source-Generated JSON Context for Event Data Types
// =============================================================================
// Provides trim-safe JSON deserialization for the closed set of event data types
// used by the MVU event handler system.
//
// In trimmed WASM builds, reflection-based JsonSerializer.Deserialize is
// disabled. This source-generated context pre-computes the serialization
// metadata at compile time, enabling the event handler system to deserialize
// event data from the browser without any reflection.
//
// See also:
//   - EventData.cs  — the event data record types
//   - Events.cs     — the on<T> helper that binds deserializers to handlers
//   - HandlerRegistry.cs — dispatches events using handler-carried deserializers
// =============================================================================

using System.Text.Json.Serialization;

namespace Abies.Html;

/// <summary>
/// Source-generated JSON serializer context for all event data types.
/// </summary>
/// <remarks>
/// This context covers the closed set of event data types that flow from
/// the browser's <c>buildEventData</c> function in <c>abies.js</c> through
/// the .NET event handler system. Because the set is fixed (defined entirely
/// within the Abies project), a single source-generated context handles all cases.
/// </remarks>
[JsonSerializable(typeof(InputEventData))]
[JsonSerializable(typeof(KeyEventData))]
[JsonSerializable(typeof(PointerEventData))]
[JsonSerializable(typeof(ScrollEventData))]
[JsonSerializable(typeof(GenericEventData))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AbiesEventJsonContext : JsonSerializerContext;
