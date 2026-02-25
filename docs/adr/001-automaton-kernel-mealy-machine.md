# ADR-001: Automaton Kernel as Mealy Machine

**Status:** Accepted  
**Date:** 2025-06-01  
**Deciders:** Maurice Peters

## Context

We need a single, minimal abstraction that unifies three widely-used architectural patterns — Model-View-Update (MVU), Event Sourcing (ES), and the Actor Model — so that domain logic can be written once and executed by any of them.

These three patterns appear different on the surface:

| Pattern | Origin | Primary concern |
|---------|--------|-----------------|
| MVU | Elm Architecture (Czaplicki, 2012) | UI rendering loops |
| Event Sourcing | Domain-Driven Design (Young, 2005) | Persistent event streams |
| Actor Model | Hewitt, Bishop & Steiger, 1973 | Concurrent message passing |

Yet all three share the same core operation: given a current state and an input, produce a new state and an output.

## Decision

The kernel is a **Mealy machine** — a deterministic finite-state transducer where outputs depend on both the current state and the input:

```
transition : (State × Event) → (State × Effect)
```

In C#:

```csharp
public interface Automaton<TState, TEvent, TEffect>
{
    static abstract (TState State, TEffect Effect) Init();
    static abstract (TState State, TEffect Effect) Transition(TState state, TEvent @event);
}
```

Two static abstract methods. Zero dependencies. Everything else is runtime.

## Mathematical Grounding

### Mealy Machine (George H. Mealy, 1955)

A Mealy machine is a 6-tuple $(S, S_0, \Sigma, \Lambda, T, G)$ where:

- $S$ — finite set of states
- $S_0 \in S$ — initial state
- $\Sigma$ — input alphabet (events)
- $\Lambda$ — output alphabet (effects)
- $T : S \times \Sigma \to S$ — state transition function
- $G : S \times \Sigma \to \Lambda$ — output function

In Automaton, $T$ and $G$ are fused into a single function that returns both the new state and the output:

$$\text{transition} : S \times \Sigma \to S \times \Lambda$$

This is a **product** of $T$ and $G$:

$$\text{transition}(s, e) = (T(s, e),\; G(s, e))$$

### Why Mealy, Not Moore?

A **Moore machine** has outputs that depend only on state ($G : S \to \Lambda$), not on the input. This is strictly less expressive for our purposes:

- In MVU, the effects produced depend on *which event* occurred (e.g., an HTTP request triggered by a button click, not just "we're in the loading state").
- In ES, the events stored depend on the command (input) that produced them.
- In Actors, the messages sent depend on the incoming message.

A Mealy machine is the minimal structure that captures input-dependent outputs.

### Determinism

The transition function is **total and deterministic**: for every $(s, e)$ pair, exactly one $(s', f)$ is produced. This is essential for:

- **Testability** — given the same state and event, the same result is always produced.
- **Event Sourcing** — replaying events must produce the same state.
- **Referential transparency** — the function is pure; side effects are described as data (`TEffect`) and executed externally by the runtime.

### Historical Lineage

```
Mealy Machine (1955)
    ↓
Actor Model (Hewitt, Bishop & Steiger, 1973)
    — "An actor processes a message and produces: a new behavior, messages to send, actors to create"
    — This IS transition : (Behavior × Message) → (Behavior' × Effects)
    ↓
Erlang/OTP (Armstrong, 1986)
    — gen_server: handle_call(Request, State) → {Reply, NewState}
    — gen_statem: explicit Mealy/Moore machine abstractions
    ↓
Event Sourcing (Young, ~2005)
    — apply(state, event) → state' + effects are commands/notifications
    ↓
Elm Architecture (Czaplicki, 2012)
    — update(msg, model) → (model', cmd)
    — Explicitly a Mealy machine with Cmd as the effect type
    ↓
Automaton (2025)
    — Unification: one interface, three runtimes
```

### Relation to Category Theory

The Automaton kernel is a **coalgebra** of the functor $F(X) = (X \times \Lambda)^\Sigma$:

$$\alpha : S \to (S \times \Lambda)^\Sigma$$

This means: given a state, for every possible input, we get a next state and an output. Coalgebras of polynomial functors are the standard categorical model for state machines.

## Consequences

### Positive

- **Write once, run everywhere** — the same `Transition` function drives a browser UI, an event store, and a mailbox actor.
- **Pure by construction** — effects are data, not side effects. The runtime executes them.
- **Testable in isolation** — no framework, no infrastructure, just call `Transition` directly.
- **Composable** — automata can be composed via product (parallel) or coproduct (choice).

### Negative

- **No built-in concurrency model** — the kernel is sequential. Concurrency is a runtime concern (Actor mailbox, async effects).
- **Finite-state assumption** — the state space is conceptually finite (though in practice `TState` can be any type, including unbounded records).
- **Learning curve** — developers must understand the separation between pure transitions and effectful runtimes.

### Neutral

- The kernel does not prescribe how effects are executed — that is entirely the runtime's responsibility.
- The kernel does not prescribe state persistence — Event Sourcing adds that as a runtime concern.

## References

- Mealy, G. H. (1955). "A Method for Synthesizing Sequential Circuits." *Bell System Technical Journal*, 34(5), 1045–1079.
- Hewitt, C., Bishop, P., & Steiger, R. (1973). "A Universal Modular ACTOR Formalism for Artificial Intelligence." *IJCAI*.
- Czaplicki, E. (2012). "Elm: Concurrent FRP for Functional GUIs." Senior thesis, Harvard University.
- Rutten, J. J. M. M. (2000). "Universal coalgebra: a theory of systems." *Theoretical Computer Science*, 249(1), 3–80.
