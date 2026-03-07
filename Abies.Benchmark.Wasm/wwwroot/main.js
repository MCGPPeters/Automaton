// =============================================================================
// main.js — WASM Bootstrap for Abies Benchmark
// =============================================================================
// The .NET WASM runtime exports a builder object from dotnet.js.
// This script imports it and calls .run() to start the C# Main() method.
// =============================================================================

import { dotnet } from './_framework/dotnet.js';

await dotnet.run();
