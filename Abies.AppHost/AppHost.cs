// =============================================================================
// Aspire AppHost — Orchestration for the Conduit Application
// =============================================================================
// Orchestrates:
//   - KurrentDB (EventStoreDB) — event store for User + Article aggregates
//   - PostgreSQL — read model projections
//   - Conduit API — the REST API backend
// =============================================================================

var builder = DistributedApplication.CreateBuilder(args);

// ─── Infrastructure Resources ──────────────────────────────────────────────

var kurrentDb = builder.AddContainer("kurrentdb", "eventstore/eventstore", "lts")
    .WithEnvironment("EVENTSTORE_INSECURE", "true")
    .WithEnvironment("EVENTSTORE_RUN_PROJECTIONS", "All")
    .WithEnvironment("EVENTSTORE_START_STANDARD_PROJECTIONS", "true")
    .WithEnvironment("EVENTSTORE_MEM_DB", "true")
    .WithHttpEndpoint(port: 2113, targetPort: 2113, name: "http")
    .WithEndpoint(port: 1113, targetPort: 1113, name: "tcp");

var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin();

var conduitDb = postgres.AddDatabase("conduitdb");

// ─── Application Projects ──────────────────────────────────────────────────

builder.AddProject<Projects.Abies_Conduit_Api>("conduit-api")
    .WithReference(conduitDb)
    .WaitFor(conduitDb)
    .WithEnvironment("ConnectionStrings__kurrentdb", "esdb://localhost:2113?tls=false")
    .WaitFor(kurrentDb);

builder.Build().Run();
