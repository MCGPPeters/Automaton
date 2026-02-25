# ADR-002: Shared Runtime as Monadic Left Fold

**Status:** Accepted  
**Date:** 2025-06-01  
**Deciders:** Maurice Peters

## Context

The Automaton kernel (ADR-001) defines a pure transition function. But real applications need side effects: rendering UIs, persisting events, sending messages, executing HTTP requests. We need an execution model that:

1. Runs the transition function on each incoming event
2. Allows observation of each transition (for rendering, persistence, logging)
3. Allows effects to produce feedback events (closing the loop)
4. Is structurally identical across all runtimes — only the observer and interpreter differ

## Decision

The shared runtime is a **monadic left fold** over an event stream, parameterized by two extension points:

| Extension Point | Signature | Purpose |
|----------------|-----------|---------|
| **Observer** | `(State, Event, Effect) → Task` | See each transition triple (render, persist, log) |
| **Interpreter** | `Effect → Task<IEnumerable<Event>>` | Convert effects to feedback events |

```csharp
public delegate Task Observer<in TState, in TEvent, in TEffect>(
    TState state, TEvent @event, TEffect effect);

public delegate Task<IEnumerable<TEvent>> Interpreter<in TEffect, TEvent>(TEffect effect);
```

The runtime loop:

```
Dispatch(event) → Transition(state, event) → (state', effect) → Observe → Interpret → [feedback events]
```

## Mathematical Grounding

### Left Fold (Catamorphism)

A **left fold** reduces a sequence to a single value by applying a binary function from left to right:

$$\text{foldl} : (B \to A \to B) \to B \to [A] \to B$$

Applied to state machines:

$$\text{state} = \text{foldl}\;\text{transition}\;\text{init}\;\text{events}$$

This is exactly Event Sourcing's state reconstruction:

```csharp
var state = events.Aggregate(seed, (s, e) => Transition(s, e).State);
```

### Monadic Left Fold

A **monadic left fold** lifts the fold into a monad $M$ (in our case, `Task`):

$$\text{foldM} : (B \to A \to M\;B) \to B \to [A] \to M\;B$$

This allows each step to perform effects (the observer and interpreter are effectful). The runtime is:

$$\text{foldM}\;(\lambda\;s\;e \to \text{let}\;(s', f) = T(s, e)\;\text{in}\;\text{observe}(s', e, f) \gg \text{interpret}(f) \gg \text{return}\;s')\;s_0\;\text{events}$$

In plain language: for each event, transition, then observe, then interpret effects (which may produce more events that recurse back into the fold).

### Observer as Writer Monad

The observer accumulates side effects alongside state transitions. This is the **Writer monad** pattern:

$$\text{Writer}\;W\;A = (A, W)$$

Where $W$ is a monoid of observations (logs, renders, persisted events). The `Then` combinator for composing observers corresponds to the monoidal append:

```csharp
public static Observer<S, E, F> Then<S, E, F>(
    this Observer<S, E, F> first, Observer<S, E, F> second) =>
    async (state, @event, effect) =>
    {
        await first(state, @event, effect);
        await second(state, @event, effect);
    };
```

This satisfies the monoid laws:
- **Identity**: a no-op observer (`(_, _, _) => Task.CompletedTask`) is the identity element
- **Associativity**: `(a.Then(b)).Then(c)` ≡ `a.Then(b.Then(c))` — both execute a, b, c in order

### Interpreter as Kleisli Arrow

The interpreter converts effects to feedback events:

$$\text{interpret} : \Lambda \to M\;[\Sigma]$$

This is a **Kleisli arrow** in the `Task` monad, lifting a pure effect description into an effectful computation that produces events. The feedback loop (interpret → dispatch feedback events → more transitions) creates a **free monad**-like structure where effects are descriptions reified as data.

### How Each Runtime Instantiates the Fold

All three runtimes are the *same* fold with different observer and interpreter:

| Runtime | Observer | Interpreter | Character |
|---------|----------|-------------|-----------|
| **MVU** | Render state → view | Execute commands → feedback events | Interactive loop |
| **Event Sourcing** | Append event to store | No-op (empty) | Persistent log |
| **Actor** | No-op (state is internal) | Execute effect via self-reference | Concurrent mailbox |

The structural identity is the key insight: swapping the observer/interpreter transforms the behavior while preserving the core loop.

## Consequences

### Positive

- **Single implementation** — `AutomatonRuntime` is written once and shared by all three runtimes.
- **Separation of concerns** — the transition function is pure; the observer and interpreter handle effects.
- **Composable observers** — `Then` allows stacking render + log + metrics without modifying the runtime.
- **Testable** — inject test observers/interpreters to verify behavior without real infrastructure.

### Negative

- **Sequential processing** — events are processed one at a time. Parallelism requires the Actor runtime's mailbox.
- **Feedback loop complexity** — interpreter-produced events recurse into the fold, which can create unbounded loops if effects produce infinite event chains.
- **Async overhead** — even synchronous runtimes pay for `Task` allocation. (Acceptable given the use cases are inherently async in production.)

### Neutral

- The runtime does not prescribe event ordering beyond sequential dispatch.
- The runtime does not prescribe error handling — that is the Decider's concern (ADR-004).

## References

- Hutton, G. (1999). "A tutorial on the universality and expressiveness of fold." *Journal of Functional Programming*, 9(4), 355–372.
- Wadler, P. (1992). "The essence of functional programming." *POPL*.
- McBride, C. & Paterson, R. (2008). "Applicative programming with effects." *Journal of Functional Programming*, 18(1), 1–13.
