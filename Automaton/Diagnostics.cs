// =============================================================================
// Diagnostics — OpenTelemetry-compatible Tracing
// =============================================================================
// Provides an ActivitySource for distributed tracing instrumentation.
//
// Library authors instrument with System.Diagnostics.ActivitySource (zero
// external dependencies). Application developers opt in to collection via
// OpenTelemetry SDK or any other listener.
//
// To enable tracing in your application, register the source name:
//
//     builder.Services.AddOpenTelemetry()
//         .WithTracing(tracing => tracing.AddSource(AutomatonDiagnostics.SourceName));
//
// When no listener is registered, ActivitySource.StartActivity() returns null
// and the instrumentation has near-zero overhead.
// =============================================================================

using System.Diagnostics;

namespace Automaton;

/// <summary>
/// OpenTelemetry-compatible tracing for the Automaton runtime.
/// </summary>
/// <remarks>
/// <para>
/// Exposes a single <see cref="ActivitySource"/> that all runtime components
/// use to emit spans. Application developers enable collection by registering
/// <see cref="SourceName"/> with their telemetry pipeline.
/// </para>
/// <para>
/// This class uses only <c>System.Diagnostics</c> APIs — no external
/// OpenTelemetry packages are required. Any OpenTelemetry-compatible
/// collector will pick up the spans automatically.
/// </para>
/// </remarks>
public static class AutomatonDiagnostics
{
    /// <summary>
    /// The ActivitySource name. Use this to register the source with your
    /// telemetry pipeline (e.g., <c>AddSource(AutomatonDiagnostics.SourceName)</c>).
    /// </summary>
    public const string SourceName = "Automaton";

    /// <summary>
    /// The shared ActivitySource for all Automaton runtime tracing.
    /// </summary>
    internal static ActivitySource Source { get; } = new(
        SourceName,
        typeof(AutomatonDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
