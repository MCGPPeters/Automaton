// =============================================================================
// ConduitStaticCollection — xUnit Collection for Static E2E Tests
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;

namespace Abies.Conduit.Testing.E2E;

[CollectionDefinition("ConduitStatic")]
public sealed class ConduitStaticCollection : ICollectionFixture<ConduitStaticFixture>;
