# ADR-004: Decider Pattern for Command Validation

**Status:** Accepted  
**Date:** 2025-06-01  
**Deciders:** Maurice Peters

## Context

The Automaton kernel (ADR-001) defines a pure transition function `(State × Event) → (State × Effect)`. Events are *facts* — things that have already happened. But in many domains, the input is not a fact but an *intent* — a command that may be invalid given the current state.

Examples:
- "Add 200 to a counter with max 100" — should be rejected, not silently overflow
- "Reset a counter already at zero" — should be rejected as a no-op error
- "Place an order for an out-of-stock item" — should fail, not create an invalid event

The kernel's `Transition` is a **total function** — it always succeeds. We need a layer that can *reject* invalid inputs before they become events.

## Decision

Adopt the **Decider pattern** (Chassaing, 2021) as an extension of the Automaton kernel:

```csharp
public interface Decider<TState, TCommand, TEvent, TEffect, TError>
    : Automaton<TState, TEvent, TEffect>
{
    static abstract Result<IEnumerable<TEvent>, TError> Decide(TState state, TCommand command);
    static virtual bool IsTerminal(TState state) => false;
}
```

The pipeline becomes:

```
Command → Decide(state) → Result<Events, Error> → [Transition(state, event)]* → (State', Effect)
```

Together with the Automaton's `Init` and `Transition`, this gives the **seven elements** of the Decider:

| # | Element | Provider | Method |
|---|---------|----------|--------|
| 1 | Command type | Type parameter | `TCommand` |
| 2 | Event type | Type parameter | `TEvent` |
| 3 | State type | Type parameter | `TState` |
| 4 | Initial state | Automaton | `Init()` |
| 5 | Decide | Decider | `Decide(state, command)` |
| 6 | Evolve | Automaton | `Transition(state, event)` |
| 7 | Is terminal | Decider | `IsTerminal(state)` |

## Mathematical Grounding

### Decide as Kleisli Arrow

The `Decide` function has the signature:

$$\text{decide} : S \times C \to T + E$$

Where $T = [\Sigma]$ (list of events) and $E$ is the error type. Currying the state:

$$\text{decide} : C \to \text{Reader}\langle S,\; \text{Result}\langle [\Sigma], E \rangle \rangle$$

This is a **Kleisli arrow** in the `Result` monad composed with the `Reader` monad:

$$\text{decide} : C \to S \to T + E$$

The Kleisli composition allows chaining validations:

```
validateFormat >==> validateBusinessRules >==> validateCapacity
```

Each step can short-circuit on error (via `Result.Bind`), and the final result is either the accumulated events or the first error encountered.

### Command vs Event: Intent vs Fact

The distinction is fundamental:

| Aspect | Command (intent) | Event (fact) |
|--------|-----------------|--------------|
| **Tense** | Imperative ("Add 5") | Past tense ("Added 5") |
| **Validity** | May be rejected | Always valid (already happened) |
| **Cardinality** | One command → 0..N events | One event → exactly one transition |
| **Idempotency** | `Decide` may return `Ok([])` (accepted, nothing happened) | Events are never empty — they record that something *did* happen |
| **Persistence** | Never persisted | Always persisted (in ES) |

Mathematically:
- **Events** are elements of the input alphabet $\Sigma$ — they drive the Mealy machine
- **Commands** are elements of a separate alphabet $C$ — they are *validated* before producing events
- **Decide** is the function $C \times S \to [\Sigma] + E$ that bridges the two alphabets

### IsTerminal: Automaton Lifecycle

`IsTerminal` is a **predicate on states** that signals the automaton has reached an absorbing state:

$$\text{isTerminal} : S \to \mathbb{B}$$

In automata theory, terminal (or accepting/final) states are the states where no further transitions should occur. For the Decider, this means:
- No further commands are accepted
- Infrastructure can archive/dispose the aggregate
- The lifecycle is complete (e.g., order shipped, game ended)

The default implementation returns `false` (never terminal), making this opt-in.

### Backward Compatibility via Subtyping

Since `Decider<S, C, E, F, Err> : Automaton<S, E, F>`, upgrading from Automaton to Decider is **non-breaking**. The Liskov Substitution Principle holds:

$$\text{Decider}\langle S, C, E, F, Err \rangle <: \text{Automaton}\langle S, E, F \rangle$$

Any code expecting an `Automaton` will work with a `Decider`. The `Decide` and `IsTerminal` methods are strictly additive.

### The Seven Elements as a Mathematical Structure

The Decider's seven elements form a **coalgebra with decision**:

1. **State space** $S$ — the set of all possible states
2. **Command alphabet** $C$ — the set of all possible commands
3. **Event alphabet** $\Sigma$ — the set of all possible events
4. **Initial state** $s_0 \in S$ — the starting configuration
5. **Decision function** $\delta : S \times C \to [\Sigma] + E$ — validates and produces events
6. **Evolution function** $T : S \times \Sigma \to S \times \Lambda$ — the Mealy transition
7. **Terminal predicate** $\pi : S \to \mathbb{B}$ — identifies final states

The composition of decide-then-evolve gives the full pipeline:

$$\text{handle}(s, c) = \delta(s, c) \gg\!\!= \lambda\;\text{events} \to \text{foldl}\;T\;s\;\text{events}$$

This is a Kleisli composition of the decision function with the fold over the transition function.

## Consequences

### Positive

- **Intent separated from fact** — commands can be rejected; events are always valid.
- **Pure validation** — `Decide` is a pure function, trivially testable.
- **Composable with all runtimes** — `Decider : Automaton` means it works everywhere Automaton works.
- **Domain errors are explicit** — `Result<Events, Error>` forces handling of both outcomes.
- **Lifecycle management** — `IsTerminal` enables infrastructure-level aggregate lifecycle control.

### Negative

- **Additional type parameters** — `Decider<TState, TCommand, TEvent, TEffect, TError>` has five type parameters vs Automaton's three. This increases signature verbosity.
- **Two-step design** — developers must think about both the command→event mapping (Decide) and the event→state mapping (Transition), rather than a single step.
- **Not all domains need it** — simple UI state machines (MVU) rarely need command validation. The Decider is optional — Automaton remains the simpler choice.

### Neutral

- The Decider does not prescribe *where* validation data comes from — external data (time, exchange rates) must be included in the command before calling Decide.
- The Decider does not prescribe error aggregation — a single error is returned per Decide call. Collecting multiple validation errors requires structuring `TError` appropriately.

## References

- Chassaing, J. (2021). "Functional Event Sourcing Decider." [thinkbeforecoding.com](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider).
- Young, G. (2010). "CQRS Documents." [cqrs.files.wordpress.com](https://cqrs.files.wordpress.com/2010/11/cqrs_documents.pdf).
- Moggi, E. (1991). "Notions of computation and monads." *Information and Computation*, 93(1), 55–92.
