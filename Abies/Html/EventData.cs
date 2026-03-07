// =============================================================================
// Event Data Types
// =============================================================================
// Platform-independent data types for DOM events. These records match the JSON
// payloads built by the JavaScript event handler (buildEventData in abies.js).
//
// The types use System.Text.Json.Serialization attributes for trim-safe
// deserialization in .NET 10+ WebAssembly.
//
// Type hierarchy:
//   InputEventData   — for input/change events (value, checked)
//   KeyEventData     — for keyboard events (key, modifiers)
//   PointerEventData — for mouse/pointer events (coordinates, button)
//   ScrollEventData  — for scroll events (position, dimensions)
//   GenericEventData — catch-all with all common fields
// =============================================================================

using System.Text.Json.Serialization;

namespace Abies.Html;

/// <summary>Data for input events.</summary>
public record InputEventData(
    [property: JsonPropertyName("value")] string? Value);

/// <summary>Data for keyboard events.</summary>
public record KeyEventData(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("repeat")] bool Repeat,
    [property: JsonPropertyName("altKey")] bool AltKey,
    [property: JsonPropertyName("ctrlKey")] bool CtrlKey,
    [property: JsonPropertyName("shiftKey")] bool ShiftKey);

/// <summary>Data for pointer or mouse events.</summary>
public record PointerEventData(
    [property: JsonPropertyName("clientX")] double ClientX,
    [property: JsonPropertyName("clientY")] double ClientY,
    [property: JsonPropertyName("button")] int Button);

/// <summary>
/// Data for scroll events on scrollable elements.
/// Contains scroll position and dimensions needed for virtualization.
/// </summary>
public record ScrollEventData(
    [property: JsonPropertyName("scrollTop")] double ScrollTop,
    [property: JsonPropertyName("scrollLeft")] double ScrollLeft,
    [property: JsonPropertyName("scrollHeight")] double ScrollHeight,
    [property: JsonPropertyName("scrollWidth")] double ScrollWidth,
    [property: JsonPropertyName("clientHeight")] double ClientHeight,
    [property: JsonPropertyName("clientWidth")] double ClientWidth);

/// <summary>
/// Generic event data encompassing all common fields.
/// Used as a catch-all when a specialized type isn't needed.
/// </summary>
public record GenericEventData(
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("checked")] bool? Checked,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("repeat")] bool? Repeat,
    [property: JsonPropertyName("altKey")] bool AltKey,
    [property: JsonPropertyName("ctrlKey")] bool CtrlKey,
    [property: JsonPropertyName("shiftKey")] bool ShiftKey,
    [property: JsonPropertyName("clientX")] double? ClientX,
    [property: JsonPropertyName("clientY")] double? ClientY,
    [property: JsonPropertyName("button")] int? Button);
