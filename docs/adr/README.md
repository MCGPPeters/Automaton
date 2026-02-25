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
| [003](003-result-type-algebraic-sum.md) | Result Type as Algebraic Sum Type | Accepted | `Result<T, E> ≅ T + E` with functor (Map), monad (Bind), exhaustive matching |
| [004](004-decider-pattern-command-validation.md) | Decider Pattern for Command Validation | Accepted | Decide is a Kleisli arrow: `Command → State → Result<Events, Error>` |

### Runtimes

| ADR | Title | Status | Constraint | Entry Point |
|-----|-------|--------|------------|-------------|
| [005](005-mvu-runtime-automaton-dispatch.md) | MVU Runtime | Accepted | `Automaton` | `Dispatch(event)` |
| [006](006-event-sourcing-runtime-decider-handle.md) | Event Sourcing Runtime | Accepted | `Decider` | `Handle(command)` |
| [007](007-actor-runtime-automaton-tell.md) | Actor Runtime | Accepted | `Automaton` | `Tell(message)` |

## Mathematical Concepts by ADR

| Concept | ADR | Application |
|---------|-----|-------------|
| Mealy machine (1955) | 001 | Kernel abstraction |
| Coalgebra of polynomial functor | 001 | Categorical model of state machines |
| Left fold / catamorphism | 002 | Runtime execution model, ES state reconstruction |
| Monadic left fold (foldM) | 002 | Effectful event processing |
| Writer monad / monoid | 002 | Observer composition via `Then` |
| Kleisli arrow | 002, 004 | Interpreter, Decide function |
| Sum type / coproduct | 003 | `Result<T, E> ≅ T + E` |
| Functor | 003 | `Result.Map` |
| Monad | 003 | `Result.Bind` |
| Bifunctor | 003 | `Result.MapError` |
| Free monoid | 006 | Event store as `[Event]` |
| Linearizability | 007 | Sequential actor message processing |

## Decision Summary: Why Each Runtime Uses Its Mechanism

| Runtime | Constraint | Entry Point | Why |
|---------|------------|-------------|-----|
| **MVU** | Automaton | `Dispatch(event)` | UI messages ARE events (facts). Transition is total — never rejects. Validation errors become state, rendered by the view. |
| **Event Sourcing** | Decider | `Handle(command)` | Events are persisted facts — they MUST be validated before storage. Decide rejects invalid commands. No undo in an append-only store. |
| **Actor** | Automaton | `Tell(message)` | Fire-and-forget — no synchronous error channel. Validation inside Transition (errors become state). Matches Hewitt's original model. |
