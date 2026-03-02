# Automaton.Patterns

> **Status:** Coming soon — this package is under active development on a separate branch and will be published once [PR #8](https://github.com/MCGPPeters/Automaton/pull/8) merges.

## What Is It?

**Automaton.Patterns** extends the core Automaton library with production-grade building blocks for event-sourced systems. While the core package gives you the kernel (Automaton, Decider, Runtime, Result), the Patterns package provides the infrastructure layer.

## Planned Contents

### EventStore

A functional abstraction over an append-only event stream. Works with any storage backend.

```csharp
// The interface (simplified sketch)
public abstract class EventStore
{
    public abstract IAsyncEnumerable<StoredEvent<TEvent>> Load<TEvent>(
        string streamId, CancellationToken ct = default);

    public abstract Task<long> Append<TEvent>(
        string streamId, long expectedVersion,
        IReadOnlyList<TEvent> events, CancellationToken ct = default);
}
```

### AggregateRunner

Orchestrates the decide-then-append cycle: load events → fold state → decide → append new events. The canonical event-sourcing workflow built on top of `DecidingRuntime`.

### ResolvingAggregateRunner

An `AggregateRunner` that automatically resolves optimistic concurrency conflicts by retrying with a configurable `ConflictResolver` strategy.

### ConflictResolver

Pluggable strategy for resolving version conflicts during concurrent writes:

- **LastWriteWins** — always retry
- **Custom** — inspect conflicting events and decide whether to retry, skip, or fail

### Saga / Process Manager

Coordinates long-running business processes across multiple aggregates using a state machine (itself an Automaton).

## When to Use Patterns

| Scenario | Package |
| -------- | ------- |
| Learning, prototyping, simple state machines | **Automaton** (core) |
| In-memory MVU, Actor, or custom runtimes | **Automaton** (core) |
| Production event sourcing with persistence | **Automaton.Patterns** |
| Optimistic concurrency with retry | **Automaton.Patterns** |
| Cross-aggregate sagas | **Automaton.Patterns** |

## Getting Started (Preview)

Once published, installation will be:

```shell
dotnet add package Automaton.Patterns
```

Full documentation will appear in this section after release.

## See Also

- [The Kernel](../concepts/the-kernel.md) — the foundations Patterns builds on
- [The Decider](../concepts/the-decider.md) — AggregateRunner is a DecidingRuntime specialization
- [Event-Sourced Aggregate Tutorial](../tutorials/03-event-sourced-aggregate.md) — uses the in-test-project version
