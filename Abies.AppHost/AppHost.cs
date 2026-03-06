/// <summary>
/// Aspire AppHost for orchestrating Abies applications.
/// Currently a skeleton — will be extended in Phase 13 (Conduit)
/// to orchestrate the API backend, database, and WASM frontend.
///
/// For js-framework-benchmark, use the benchmark's own npm server
/// (see scripts/run-benchmark.sh).
/// </summary>
var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();
