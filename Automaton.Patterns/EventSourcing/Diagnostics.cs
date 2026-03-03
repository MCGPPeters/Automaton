// =============================================================================
// EventSourcing Diagnostics — OpenTelemetry-compatible Tracing
// =============================================================================
// Provides an ActivitySource for Event Sourcing instrumentation.
// Follows the same pattern as AutomatonDiagnostics in the kernel.
//
// To enable tracing:
//
//     builder.Services.AddOpenTelemetry()
//         .WithTracing(tracing => tracing
//             .AddSource(AutomatonDiagnostics.SourceName)
//             .AddSource(EventSourcingDiagnostics.SourceName));
// =============================================================================

using System.Diagnostics;

namespace Automaton.Patterns.EventSourcing;

/// <summary>
/// OpenTelemetry-compatible tracing for Event Sourcing components.
/// </summary>
/// <remarks>
/// <para>
/// Exposes a dedicated <see cref="ActivitySource"/> for Event Sourcing operations
/// (event store append/load, aggregate handle, projections). Register
/// <see cref="SourceName"/> alongside <c>AutomatonDiagnostics.SourceName</c>
/// in your telemetry pipeline for full tracing.
/// </para>
/// <para>
/// Uses only <c>System.Diagnostics</c> APIs — no external OpenTelemetry
/// packages required.
/// </para>
/// </remarks>
public static class EventSourcingDiagnostics
{
    /// <summary>
    /// The ActivitySource name for Event Sourcing operations.
    /// </summary>
    public const string SourceName = "Automaton.Patterns.EventSourcing";

    /// <summary>
    /// The shared ActivitySource for all Event Sourcing tracing.
    /// </summary>
    internal static ActivitySource Source { get; } = new(
        SourceName,
        typeof(EventSourcingDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
