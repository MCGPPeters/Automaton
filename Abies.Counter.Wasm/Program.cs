// =============================================================================
// Counter WASM Bootstrap — Program.cs
// =============================================================================
// Entry point for the Abies Counter WebAssembly application.
//
// Startup sequence:
//   1. main.js loads the .NET WASM runtime and wires the dispatch callback
//   2. main.js calls dotnet.run() → this Main method
//   3. We import abies.js as a .NET JS module (same instance main.js loaded)
//   4. We create the browser-specific Apply delegate (binary batch → interop)
//   5. We start the AbiesRuntime which renders the initial view
//
// The Apply delegate is the only browser-specific code. Everything else is
// platform-agnostic and could run in tests or on a server.
// =============================================================================

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Abies;
using Abies.Counter;
using Automaton;

[SupportedOSPlatform("browser")]
internal static class Program
{
    /// <summary>
    /// Application entry point — called by dotnet.run() from main.js.
    /// </summary>
    private static async Task Main()
    {
        // Load the abies.js module for [JSImport] interop.
        // Path is relative to the calling module (_framework/dotnet.runtime.js),
        // so "../abies.js" navigates up from _framework/ to the wwwroot root.
        // main.js has already imported abies.js as an ES module; the browser
        // caches modules by URL, so this returns the same instance.
        await JSHost.ImportAsync("Abies", "../abies.js");

        // Create the binary batch writer and the browser-specific Apply delegate.
        // This is the seam between the pure Abies core and the real DOM:
        //   patches → binary batch → JS interop → DOM mutations
        var batchWriter = new RenderBatchWriter();

        void BrowserApply(IReadOnlyList<Patch> patches)
        {
            var binaryData = batchWriter.Write(patches);

            // ReadOnlyMemory<byte> → Span<byte> for JSType.MemoryView interop.
            // The underlying store is ArrayBufferWriter<byte>'s byte[], so
            // MemoryMarshal.TryGetArray always succeeds.
            MemoryMarshal.TryGetArray(binaryData, out var segment);
            Interop.ApplyBinaryBatch(segment.Array.AsSpan(segment.Offset, segment.Count));
        }

        // Start the MVU runtime.
        // AbiesRuntime.Start handles the full startup sequence:
        //   Initialize → View → Diff → Apply (renders via AddRoot patch) → Subscriptions
        // The interpreter is a no-op since the counter has no commands.
        var runtime = await AbiesRuntime<CounterProgram, CounterModel, Unit>.Start(
            apply: BrowserApply,
            interpreter: _ => new ValueTask<Result<Message[], PipelineError>>(
                Result<Message[], PipelineError>.Ok([])),
            titleChanged: Interop.SetTitle);

        // Keep the application alive (WASM main must not return).
        await Task.Delay(Timeout.Infinite);
    }
}
