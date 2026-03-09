// =============================================================================
// ConduitAutoCollection — xUnit Collection for InteractiveAuto E2E Tests
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;

namespace Abies.Conduit.Testing.E2E;

[CollectionDefinition("ConduitAuto")]
public sealed class ConduitAutoCollection : ICollectionFixture<ConduitAutoFixture>;
