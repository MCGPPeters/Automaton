# Tutorials

Step-by-step guides for building systems with the Automaton kernel.

## Prerequisites

- .NET 10.0 SDK
- `dotnet add package Automaton`

## Tutorials

| # | Tutorial | What You'll Build |
|---|----------|-------------------|
| 01 | [Getting Started](01-getting-started.md) | Your first automaton — a counter with state, events, and effects |
| 02 | [Building an MVU Runtime](02-mvu-runtime.md) | A Model-View-Update loop that renders views and handles effects |
| 03 | [Building an Event-Sourced Aggregate](03-event-sourced-aggregate.md) | A command-driven aggregate with event store, replay, and projections |
| 04 | [Building an Actor System](04-actor-system.md) | A mailbox actor with channels, fire-and-forget messaging, and effect callbacks |
| 05 | [Command Validation with the Decider](05-command-validation.md) | Domain validation using the Decider pattern and the Result type |
| 06 | [Observability with OpenTelemetry](06-observability.md) | Distributed tracing with zero external dependencies |

## The Big Idea

Every tutorial builds on the **same kernel**:

```text
transition : (State × Event) → (State × Effect)
```

You write your domain logic once as a pure transition function. Each tutorial shows a different runtime that executes that function — MVU, Event Sourcing, or Actors — without changing a single line of domain code.

## Recommended Reading Order

1. Start with **[Getting Started](01-getting-started.md)** — this introduces the kernel and the shared runtime.
2. Pick any of tutorials 02–04 based on your runtime pattern.
3. Read **[Command Validation](05-command-validation.md)** when you need to validate commands before producing events.
4. Add **[Observability](06-observability.md)** when you're ready for production tracing.

## See Also

- [Architecture Decision Records](../adr/) — design rationale with mathematical grounding
