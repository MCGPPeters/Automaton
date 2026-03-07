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
    .WithHttpEndpoint(targetPort: 2113, name: "http")
    .WithEndpoint(targetPort: 1113, name: "tcp")
    .WithHttpHealthCheck("/health/live", statusCode: 204);

var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin();

var conduitDb = postgres.AddDatabase("conduitdb");

// ─── Application Projects ──────────────────────────────────────────────────

// Build the KurrentDB connection string dynamically from Aspire's endpoint resolution.
// The http endpoint exposes gRPC (EventStoreDB uses HTTP/2 for gRPC on the same port).
var kurrentDbEndpoint = kurrentDb.GetEndpoint("http");

builder.AddProject<Projects.Abies_Conduit_Api>("conduit-api")
    .WithReference(conduitDb)
    .WaitFor(conduitDb)
    .WithEnvironment(context =>
    {
        context.EnvironmentVariables["ConnectionStrings__kurrentdb"] =
            ReferenceExpression.Create(
                $"esdb://{kurrentDbEndpoint.Property(EndpointProperty.Host)}:{kurrentDbEndpoint.Property(EndpointProperty.Port)}?tls=false");
    })
    .WaitFor(kurrentDb);

builder.Build().Run();
