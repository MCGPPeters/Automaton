// =============================================================================
// WASM Bootstrap — main.js
// =============================================================================
// Loads the .NET WebAssembly runtime and starts the Abies Counter application.
// This is the entry point referenced by index.html.
//
// The dotnet.js runtime is produced by the `dotnet publish` build and handles:
//   - Downloading and initializing the .NET WASM runtime
//   - Loading assemblies
//   - Providing the JS↔.NET interop bridge
//
// The Abies framework registers a global dispatch function so that the
// event delegation in abies.js can call back into .NET.
// =============================================================================

import { dotnet } from "./_framework/dotnet.js";

const { setModuleImports, getAssemblyExports, getConfig } = await dotnet
    .withDiagnosticTracing(false)
    .create();

// The [JSExport] DispatchDomEvent lives in the Abies assembly, not the main assembly.
// We need to get exports from Abies.dll specifically.
const abiesExports = await getAssemblyExports("Abies");

// Register the dispatch bridge globally so abies.js can find it.
globalThis.__abies_dispatch = (pathJson, eventName, eventData) => {
    abiesExports.Abies.Interop.DispatchDomEvent(pathJson, eventName, eventData);
};

// Start the .NET application.
await dotnet.run();
