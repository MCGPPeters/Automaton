// =============================================================================
// WASM Bootstrap — main.js
// =============================================================================
// Loads the .NET WebAssembly runtime, wires the Abies event dispatch and
// navigation callbacks, and starts the benchmark application.
//
// See Abies.Counter.Wasm/wwwroot/main.js for detailed documentation.
// =============================================================================

import { dotnet } from "./_framework/dotnet.js";
import { setDispatchCallback, setOnUrlChangedCallback, setupEventDelegation, setupNavigation } from "./abies.js";

const { getAssemblyExports } = await dotnet
    .withDiagnosticTracing(false)
    .create();

const abiesExports = await getAssemblyExports("Abies");

setDispatchCallback((commandId, eventName, eventData) =>
    abiesExports.Abies.Interop.DispatchDomEvent(commandId, eventName, eventData)
);

setOnUrlChangedCallback((url) =>
    abiesExports.Abies.Interop.OnUrlChanged(url)
);

setupEventDelegation();
setupNavigation();

await dotnet.run();
