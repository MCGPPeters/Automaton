// =============================================================================
// ConduitCollection — xUnit Collection for Shared E2E Infrastructure
// =============================================================================
// Defines an xUnit test collection that shares a single ConduitAppFixture
// across all E2E test classes. This ensures:
//   - One Aspire instance (KurrentDB + PostgreSQL + API)
//   - One WASM publish + static file server
//   - One Playwright browser instance
//
// Without this, each test class with IClassFixture<ConduitAppFixture>
// would spin up its own Aspire stack — massively wasteful.
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;

namespace Abies.Conduit.Testing.E2E;

[CollectionDefinition("Conduit")]
public sealed class ConduitCollection : ICollectionFixture<ConduitAppFixture>;
