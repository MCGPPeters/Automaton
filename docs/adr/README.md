# Architecture Decision Records

This directory contains the Architecture Decision Records (ADRs) for the Automaton project.

ADRs follow the [Nygard format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions): Context → Decision → Consequences.

Each ADR also includes a **Mathematical Grounding** section that describes the formal mathematical structures underlying the decision.

## Index

### Foundations

| ADR | Title | Status | Summary |
|-----|-------|--------|---------|
| [001](001-automaton-kernel-mealy-machine.md) | Automaton Kernel as Mealy Machine | Accepted | The kernel is a Mealy machine: `transition : (State × Event) → (State × Effect)` |
| [002](002-shared-runtime-monadic-left-fold.md) | Shared Runtime as Monadic Left Fold | Accepted | All runtimes are a monadic left fold parameterized by Observer + Interpreter |
| [003](003-result-type-algebraic-sum.md) | Result Type as Algebraic Sum Type | Accepted | `Result<T, E> ≅ T + E` with functor (Map/Select), monad (Bind/SelectMany), LINQ query syntax |
| [004](004-decider-pattern-command-validation.md) | Decider Pattern for Command Validation | Accepted | Decide is a Kleisli arrow: `Command → State → Result<Events, Error>` |

### Runtimes

| ADR | Title | Status | Constraint | Entry Point |
|-----|-------|--------|------------|-------------|
| [005](005-mvu-runtime-automaton-dispatch.md) | MVU Runtime | Accepted | `Automaton` | `Dispatch(event)` |
| [006](006-event-sourcing-runtime-decider-handle.md) | Event Sourcing Runtime | Accepted | `Decider` | `Handle(command)` |
| [007](007-actor-runtime-automaton-tell.md) | Actor Runtime | Accepted | `Automaton` | `Tell(message)` |

### Production

| ADR | Title | Status | Summary |
|-----|-------|--------|---------|
| [008](008-production-hardening-thread-safety.md) | Production Hardening | Accepted | Thread safety (SemaphoreSlim), CancellationToken, feedback depth guard (64), null safety |
| [009](009-opentelemetry-tracing-diagnostics.md) | OpenTelemetry Tracing | Accepted | Zero-dependency tracing via `System.Diagnostics.ActivitySource` with five span types |
| [010](010-example-runtimes-reference-implementations.md) | Example Runtimes as Reference Implementations | Accepted | MVU, ES, Actor moved from core library to test project |
| [011](011-performance-optimizations-allocation-reduction.md) | Performance Optimizations | Accepted | Async elision, struct Result, TEvent[] narrowing — Handle accept −28%, reject −50% |
| [012](012-linq-query-syntax-remove-match.md) | LINQ Query Syntax, Remove Match | Accepted | Add Select/SelectMany for LINQ monad comprehension, remove Match methods |

## Mathematical Concepts by ADR

| Concept | ADR | Application |
|---------|-----|-------------|
| Mealy machine (1955) | 001 | Kernel abstraction |
| Coalgebra of polynomial functor | 001 | Categorical model of state machines |
| Left fold / catamorphism | 002 | Runtime execution model, ES state reconstruction |
| Monadic left fold (foldM) | 002 | Effectful event processing |
| Writer monad / monoid | 002, 009 | Observer composition via `Then`, span emissions |
| Kleisli arrow | 002, 004 | Interpreter, Decide function |
| Sum type / coproduct | 003 | `Result<T, E> ≅ T + E` |
| Functor | 003 | `Result.Map` |
| Monad | 003, 012 | `Result.Bind`, `Result.SelectMany`, LINQ query syntax |
| Bifunctor | 003 | `Result.MapError` |
| Free monoid | 006 | Event store as `[Event]` |
| Linearizability | 007, 008 | Sequential actor message processing, SemaphoreSlim serialization |
| Well-foundedness | 008 | Feedback depth guard (bounded recursion) |
| Natural transformation | 009 | Tracing preserves runtime semantics |

## Decision Summary: Why Each Runtime Uses Its Mechanism

| Runtime | Constraint | Entry Point | Why |
|---------|------------|-------------|-----|
| **MVU** | Automaton | `Dispatch(event)` | UI messages ARE events (facts). Transition is total — never rejects. Validation errors become state, rendered by the view. |
| **Event Sourcing** | Decider | `Handle(command)` | Events are persisted facts — they MUST be validated before storage. Decide rejects invalid commands. No undo in an append-only store. |
| **Actor** | Automaton | `Tell(message)` | Fire-and-forget — no synchronous error channel. Validation inside Transition (errors become state). Matches Hewitt's original model. |

## See Also

- [Documentation Home](../index.md) — full documentation overview
- [Concepts](../concepts/index.md) — theory and mental models
- [Tutorials](../tutorials/) — step-by-step guides for building systems with the kernel
- [How-To Guides](../guides/index.md) — task-oriented recipes
- [API Reference](../reference/index.md) — full public API documentation
