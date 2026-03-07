// =============================================================================
// Diagnostics — OpenTelemetry ActivitySource for PostgreSQL Read Store
// =============================================================================

using System.Diagnostics;

namespace Abies.Conduit.ReadStore.PostgreSQL;

/// <summary>
/// OpenTelemetry diagnostics for the PostgreSQL read store.
/// </summary>
internal static class ReadStoreDiagnostics
{
    /// <summary>
    /// The <see cref="ActivitySource"/> for read store operations.
    /// </summary>
    internal static readonly ActivitySource Source = new(
        "Abies.Conduit.ReadStore.PostgreSQL",
        "1.0.0");
}
