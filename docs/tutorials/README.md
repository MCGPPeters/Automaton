# Tutorials

End-to-end walkthroughs that build complete working systems on the Automaton kernel. Each tutorial starts from scratch and ends with running, tested code.

> **New to Automaton?** Start with the [Quick Start](../getting-started/index.md) for a 5-minute introduction, then come back here for deeper dives.

## Prerequisites

- .NET 10.0 SDK ([installation guide](../getting-started/installation.md))
- `dotnet add package Automaton`

## Tutorials

| # | Tutorial | What You'll Build | Concepts Used |
|---|----------|-------------------|---------------|
| 01 | [Getting Started](01-getting-started.md) | A smart thermostat with feedback loop | [Kernel](../concepts/the-kernel.md), [Runtime](../concepts/the-runtime.md) |
| 02 | [MVU Runtime](02-mvu-runtime.md) | A Model-View-Update loop with view history | [Kernel](../concepts/the-kernel.md), [Observer composition](../guides/observer-composition.md) |
| 03 | [Event-Sourced Aggregate](03-event-sourced-aggregate.md) | Command-driven aggregate with replay and projections | [Decider](../concepts/the-decider.md), [Error handling](../guides/error-handling-patterns.md) |
| 04 | [Actor System](04-actor-system.md) | Mailbox actor with channels and fire-and-forget | [Kernel](../concepts/the-kernel.md), [Custom runtimes](../guides/building-custom-runtimes.md) |
| 05 | [Command Validation](05-command-validation.md) | Domain validation with the Decider and Result | [Decider](../concepts/the-decider.md), [Result](../reference/result.md) |
| 06 | [Observability](06-observability.md) | Distributed tracing with zero dependencies | [Diagnostics](../reference/diagnostics.md) |

## The Big Idea

Every tutorial builds on the **same kernel** — a [Mealy machine](../concepts/the-kernel.md):

```text
transition : (State × Event) → (State × Effect)
```

You write your domain logic once as a pure transition function. Each tutorial shows a different runtime that executes that function — MVU, Event Sourcing, or Actors — without changing a single line of domain code. See [Runtimes Compared](../concepts/runtimes-compared.md) for help choosing.

## Recommended Reading Order

```text
Getting Started (01)
        │
        ├──► MVU Runtime (02)
        ├──► Event-Sourced Aggregate (03) ──► Command Validation (05)
        └──► Actor System (04)
                                                      │
                                              Observability (06) ◄──┘
```

1. **[Getting Started](01-getting-started.md)** — the kernel and the shared runtime.
2. **Pick your runtime** — 02, 03, or 04 based on your pattern.
3. **[Command Validation](05-command-validation.md)** — when you need to validate before producing events.
4. **[Observability](06-observability.md)** — production tracing across all runtimes.

## What to Read Next

| If you want to… | Read |
| ---------------- | ---- |
| Understand the theory | [Concepts](../concepts/index.md) |
| Combine multiple automata | [Composition](../concepts/composition.md) |
| Solve a specific problem | [How-To Guides](../guides/index.md) |
| Look up an API | [Reference](../reference/index.md) |
| See design rationale | [Architecture Decision Records](../adr/) |
