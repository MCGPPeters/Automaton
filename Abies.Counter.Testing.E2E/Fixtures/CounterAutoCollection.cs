// =============================================================================
// CounterAutoCollection — xUnit Collection for InteractiveAuto E2E Tests
// =============================================================================

using Abies.Counter.Testing.E2E.Fixtures;

namespace Abies.Counter.Testing.E2E;

[CollectionDefinition("CounterAuto")]
public sealed class CounterAutoCollection : ICollectionFixture<CounterAutoFixture>;
