// =============================================================================
// Program.cs — Conduit WASM Bootstrap
// =============================================================================
// Entry point for the Conduit Blazor WebAssembly application.
//
// Abies.Browser.Runtime.Run handles all browser-specific wiring:
//   - Loading abies.js
//   - Setting up event delegation and navigation
//   - Creating the binary batch writer and Apply delegate
//   - Starting the MVU runtime
//   - Keeping the WASM process alive
//
// The interpreter converts ConduitCommands into HTTP API calls.
// =============================================================================

using Abies.Conduit.App;
using Automaton;

await Abies.Browser.Runtime.Run<ConduitProgram, Model, Unit>(
    interpreter: ConduitInterpreter.Interpret);
