// =============================================================================
// CounterServerCollection — xUnit Collection for InteractiveServer E2E Tests
// =============================================================================

using Abies.Counter.Testing.E2E.Fixtures;

namespace Abies.Counter.Testing.E2E;

[CollectionDefinition("CounterServer")]
public sealed class CounterServerCollection : ICollectionFixture<CounterServerFixture>;
