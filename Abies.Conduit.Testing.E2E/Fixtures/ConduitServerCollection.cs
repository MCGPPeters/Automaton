// =============================================================================
// ConduitServerCollection — xUnit Collection for InteractiveServer E2E Tests
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;

namespace Abies.Conduit.Testing.E2E;

[CollectionDefinition("ConduitServer")]
public sealed class ConduitServerCollection : ICollectionFixture<ConduitServerFixture>;
