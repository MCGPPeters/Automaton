# ADR-010: Example Runtimes as Reference Implementations

**Status:** Accepted
**Date:** 2025-06-14
**Deciders:** Maurice Peters

## Context

The Automaton library originally shipped three specialized runtimes in the core package:

- `Automaton.Mvu.MvuRuntime` — Model-View-Update runtime with render loop
- `Automaton.EventSourcing.AggregateRunner` — Event-sourced aggregate with in-memory store and projections
- `Automaton.Actor.ActorInstance` — Mailbox-based actor with `Channel<TEvent>` and `ActorRef<TEvent>`

Each demonstrated how the shared `AutomatonRuntime` (ADR-002) can be wired for a specific pattern. However, including them in the core package created several problems:

1. **Scope creep** — the core package's value proposition is the *kernel* (Automaton interface, Runtime, Decider, Result). The specialized runtimes are opinionated compositions, not foundational types.
2. **Implicit quality promise** — shipping code in the core package implies production readiness. The in-memory event store and the `DrainMailbox` heuristic (Actor) are test-grade implementations, not production infrastructure.
3. **Dependency gravity** — users who only need the kernel interface are forced to compile (and mentally navigate) runtime code they don't use.
4. **Upgrade friction** — changing a reference runtime (e.g., switching from `List<TEvent>` to a persistent store) would be a breaking change to the package, even though the implementation is illustrative.

## Decision

Move all three specialized runtimes from the core library (`Automaton/`) to the test project (`Automaton.Tests/`) as **reference implementations**:

| Runtime | From | To |
|---------|------|----|
| MVU | `Automaton/Mvu/MvuRuntime.cs` | `Automaton.Tests/Mvu/MvuRuntime.cs` |
| Event Sourcing | `Automaton/EventSourcing/EventSourcing.cs` | `Automaton.Tests/EventSourcing/EventSourcing.cs` |
| Actor | `Automaton/Actor/Actor.cs` | `Automaton.Tests/Actor/Actor.cs` |

The core package now contains exactly five files:

| File | Purpose |
|------|---------|
| `Automaton.cs` | Mealy machine interface (ADR-001) |
| `Runtime.cs` | Shared runtime — monadic left fold (ADR-002) |
| `Result.cs` | Algebraic sum type (ADR-003) |
| `Decider.cs` | Command validation interface + DecidingRuntime (ADR-004) |
| `Diagnostics.cs` | OpenTelemetry-compatible tracing (ADR-009) |

### What Changed for Each Runtime

**MVU** — moved as-is. No changes to the implementation.

**Event Sourcing** — moved and refactored. The `Handle` method was updated to use C# pattern matching (`switch` expression) instead of `Result.Match()`:

```csharp
// Before (Match-based)
public Result<TState, TError> Handle(TCommand command) =>
    TDecider.Decide(_state, command).Match<Result<TState, TError>>(
        events => { ... },
        error => Result<TState, TError>.Err(error));

// After (pattern matching)
public Result<TState, TError> Handle(TCommand command)
{
    switch (TDecider.Decide(_state, command))
    {
        case Result<TEvent[], TError>.Ok(var events):
            // ...
            return Result<TState, TError>.Ok(_state);
        case Result<TEvent[], TError>.Err(var error):
            return Result<TState, TError>.Err(error);
        default:
            throw new UnreachableException();
    }
}
```

**Actor** — moved as-is. No changes to the implementation.

### The Core Library's New Scope

The core library is the **kernel** — the minimal set of abstractions needed to define and run automatons:

```text
┌──────────────────────────────────────────────┐
│  Automaton Package (core)                    │
│                                              │
│  Automaton<S, E, F>          ← interface     │
│  AutomatonRuntime<A, S, E, F> ← engine      │
│  Decider<S, C, E, F, Err>   ← validation    │
│  DecidingRuntime<...>        ← engine        │
│  Result<T, E>                ← error type    │
│  AutomatonDiagnostics        ← tracing       │
│  Observer<S, E, F>           ← delegate      │
│  Interpreter<F, E>           ← delegate      │
└──────────────────────────────────────────────┘
```

Everything else — MVU rendering, event stores, projections, actor mailboxes — lives outside the package. Users build their own runtimes (or copy the reference implementations) using the kernel's extension points.

## Consequences

### Positive

- **Focused package** — the NuGet package contains only production-grade, hardened kernel types.
- **No false promises** — reference runtimes are clearly labeled as examples, not production infrastructure.
- **Upgrade freedom** — reference implementations can change between versions without breaking the package's public API.
- **Smaller package** — fewer types, less documentation surface, faster comprehension.

### Negative

- **Discovery** — new users must look at the test project (or documentation) to find examples of building runtimes. Mitigated by comprehensive README examples and ADRs.
- **No built-in MVU/ES/Actor** — users who want a ready-made runtime must either copy the reference implementation or build their own. This is intentional — production runtimes should be tailored to the application's infrastructure.

### Neutral

- The ADRs describing each runtime's design (005, 006, 007) remain valid — the *design decisions* are unchanged. Only the *location* of the implementations changed.
- All existing tests continue to work — they reference the implementations in their new locations.
- Future releases may publish specialized runtimes as separate NuGet packages (e.g., `Automaton.Mvu`, `Automaton.EventSourcing`) if demand warrants it.
