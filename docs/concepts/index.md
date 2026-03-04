# Concepts

This section explains the ideas behind Automaton. Read these pages to understand *why* the library works the way it does, not just *how* to use it.

## Core Ideas

| Concept | Page | One-line summary |
| ------- | ---- | ---------------- |
| The Kernel | [the-kernel.md](the-kernel.md) | Everything is a Mealy machine: `(State × Event) → (State × Effect)` |
| The Runtime | [the-runtime.md](the-runtime.md) | Observer + Interpreter = a monadic left fold over events |
| The Decider | [the-decider.md](the-decider.md) | Commands validated before events exist — Result type as the error channel |
| Composition | [composition.md](composition.md) | Combining multiple automata into one — the behavioral composition root |
| Runtimes Compared | [runtimes-compared.md](runtimes-compared.md) | When to use MVU, Event Sourcing, or Actors |
| Glossary | [glossary.md](glossary.md) | All key terms defined in plain English |

## The Core Insight

MVU, Event Sourcing, and the Actor Model look different on the surface. But they share the same mathematical structure:

```text
transition : (State × Event) → (State × Effect)
```

- In **MVU**, the event is a UI message, and the effect renders a view.
- In **Event Sourcing**, the event is a persisted fact, and the effect is a side-effect descriptor.
- In the **Actor Model**, the event is a mailbox message, and the effect sends messages to other actors.

Automaton captures this shared structure as a single interface. You write your domain logic once, and different runtimes execute it in different ways — without changing a line of domain code.

## Reading Order

If you're new to the library:

1. Start with [**The Kernel**](the-kernel.md) — understand the core abstraction
2. Then [**The Runtime**](the-runtime.md) — understand how the loop works
3. Then [**Runtimes Compared**](runtimes-compared.md) — pick the right pattern for your problem
4. Read [**The Decider**](the-decider.md) when you need command validation
5. Read [**Composition**](composition.md) when your application has multiple concerns
6. Use the [**Glossary**](glossary.md) whenever you encounter an unfamiliar term

## See Also

- [Tutorials](../tutorials/) — build real systems step by step
- [How-To Guides](../guides/) — solve specific problems
- [API Reference](../reference/) — complete type documentation
- [Architecture Decision Records](../adr/) — design rationale with mathematical grounding
