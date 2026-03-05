// =============================================================================
// WASM Bootstrap — main.js
// =============================================================================
// Loads the .NET WebAssembly runtime, wires the Abies event dispatch callback,
// and starts the Counter application.
//
// The dispatch bridge connects two module instances:
//   - abies.js (the Abies framework's browser runtime)
//   - Abies.dll (the .NET assembly with [JSExport] DispatchDomEvent)
//
// ES modules are cached by URL, so the `import` here and .NET's
// `JSHost.ImportAsync("Abies", "/abies.js")` share the same module instance.
// =============================================================================

import { dotnet } from "./_framework/dotnet.js";
import { setDispatchCallback, setupEventDelegation } from "./abies.js";

const { getAssemblyExports } = await dotnet
    .withDiagnosticTracing(false)
    .create();

// Get [JSExport] methods from the Abies assembly.
const abiesExports = await getAssemblyExports("Abies");

// Wire the dispatch bridge: abies.js event delegation → .NET DispatchDomEvent.
setDispatchCallback((commandId, eventName, eventData) =>
    abiesExports.Abies.Interop.DispatchDomEvent(commandId, eventName, eventData)
);

// Register document-level event listeners for event delegation.
setupEventDelegation();

// Start the .NET application (calls Program.Main).
await dotnet.run();
