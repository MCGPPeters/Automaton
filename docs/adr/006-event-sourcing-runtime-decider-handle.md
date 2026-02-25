# ADR-006: Event Sourcing Runtime — Decider Constraint with Handle

**Status:** Accepted  
**Date:** 2025-06-01  
**Deciders:** Maurice Peters

## Context

Event Sourcing persists every state change as an immutable event. The current state is reconstructed by replaying the event stream. We must decide:

- Should the ES runtime accept raw events (`Dispatch(event)`) or commands (`Handle(command)`)?
- What constraint should the aggregate runner require — Automaton or Decider?
- How does command validation interact with event persistence?

### The Core Insight

Event Sourcing is **fundamentally command-driven**. The flow is:

1. A command arrives (user intent)
2. The command is validated against the current state
3. If valid: events are produced and persisted
4. If invalid: nothing is persisted, an error is returned

Accepting raw events would be **architecturally wrong** because:
- Events are *facts* — they represent things that already happened
- Persisting an invalid event corrupts the event stream permanently
- There is no undo in an append-only store
- Validation must happen *before* persistence, not after

## Decision

The ES runtime uses the **Decider constraint** with `Handle(command)`:

```csharp
public sealed class AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError>
    where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError>
```

The aggregate runner implements the **decide-then-append** pattern:

```csharp
public Result<TState, TError> Handle(TCommand command) =>
    TDecider.Decide(_state, command).Match<Result<TState, TError>>(
        events =>
        {
            foreach (var @event in events)
            {
                var (newState, effect) = TDecider.Transition(_state, @event);
                _state = newState;
                _store.Append(@event);
                _effects.Add(effect);
            }
            return new Result<TState, TError>.Ok(_state);
        },
        error => new Result<TState, TError>.Err(error));
```

Key design choices:

1. **Synchronous** — no `async/await`, no `AutomatonRuntime` dependency. ES is a simple fold.
2. **Atomic** — on success, all events are appended and state is updated. On failure, nothing changes.
3. **No Observer/Interpreter** — the aggregate runner does not use the shared runtime. It directly calls `Decide` + `Transition` + `Store.Append`.

### Why Not AutomatonRuntime?

The shared `AutomatonRuntime` (ADR-002) adds two features ES doesn't need:

| Feature | AutomatonRuntime | AggregateRunner |
|---------|-----------------|-----------------|
| Observer | ✅ Async callback after each transition | ❌ Not needed — events are appended inline |
| Interpreter | ✅ Converts effects to feedback events | ❌ Not needed — ES effects are recorded, not executed |
| Async | ✅ Task-based | ❌ Synchronous — no I/O in the aggregate |
| Validation | ❌ None — accepts raw events | ✅ Decide validates before persist |

The aggregate runner is simpler and more direct: `Decide → Transition → Append`. No async overhead, no observer indirection.

## Mathematical Grounding

### State as Left Fold over Events

The fundamental equation of Event Sourcing:

$$\text{state} = \text{foldl}\;\text{transition}\;s_0\;\text{events}$$

In C#:

```csharp
var state = events.Aggregate(seed, (s, e) => Transition(s, e).State);
```

This is the **catamorphism** (fold) from ADR-002, applied to a persisted event stream. The event stream is the **single source of truth** — state is always derivable from it.

### Decide-then-Append as Kleisli Composition

The full pipeline is a composition of the decision function with the fold:

$$\text{handle}(s, c) = \delta(s, c) \gg\!\!= \lambda\;\overrightarrow{e} \to \text{append}(\overrightarrow{e}) \gg \text{foldl}\;T\;s\;\overrightarrow{e}$$

Where:
- $\delta : S \times C \to [\Sigma] + E$ is the decision function
- $\gg\!\!=$ is the monadic bind on `Result`
- $\text{append}$ persists the events
- $\text{foldl}\;T$ applies transitions to update state

On error, the bind short-circuits: nothing is appended, nothing is transitioned.

### Events as the Write-Ahead Log

The event store is a **write-ahead log** (WAL) — the log is the canonical data, and the current state is a materialized view of it. This is the same principle as database WALs, Kafka topics, and blockchain ledgers.

Algebraically, the store is a **free monoid** over the event type:

$$\text{Store} = [\Sigma] = \text{Free}\;\text{Monoid}\;\Sigma$$

- The empty list `[]` is the identity (no events = initial state)
- Concatenation `++` is the monoid operation (append new events)
- The fold is the unique monoid homomorphism from $[\Sigma]$ to $S$

### Projections as Alternative Folds

A projection builds a different view from the same event stream:

$$\text{project} : (R \to \Sigma \to R) \to R \to [\Sigma] \to R$$

This is the same left fold, but with a different accumulator type $R$ and a different fold function. The event stream supports arbitrarily many projections — each is a different catamorphism over the same list.

```csharp
var totalIncrements = new Projection<CounterEvent, int>(
    initial: 0,
    apply: (count, e) => e is CounterEvent.Increment ? count + 1 : count);
```

### Rebuild (Replay) as Proof of Correctness

`Rebuild()` replays all stored events from the initial state:

```csharp
public TState Rebuild()
{
    var (seed, _) = TDecider.Init();
    _state = _store.Replay(seed, (s, e) => TDecider.Transition(s, e).State);
    return _state;
}
```

If `Transition` is pure and deterministic (ADR-001), then:

$$\text{Rebuild}(\text{store}) \equiv \text{State}$$

This is the **correctness invariant**: the current state is always equal to the fold of the event stream. Rebuild is both a recovery mechanism and a proof.

### Hydration from Store (FromStore)

Loading an existing aggregate replays without re-validation:

```csharp
public static AggregateRunner<...> FromStore(EventStore<TEvent> store)
{
    var (seed, _) = TDecider.Init();
    var state = store.Replay(seed, (s, e) => TDecider.Transition(s, e).State);
    return new AggregateRunner<...>(state, store);
}
```

This is safe because **stored events are already validated facts**. They passed through `Decide` at write time. Replaying them through `Transition` (not `Decide`) is correct — they don't need re-validation.

### Why Commands, Not Events

| Criterion | Handle(command) | Dispatch(event) |
|-----------|----------------|-----------------|
| **Validation** | ✅ Decide validates before persist | ❌ No validation — invalid events corrupt store |
| **Atomicity** | ✅ All-or-nothing (error = no persist) | ❌ Partial append possible |
| **Domain integrity** | ✅ Only valid events enter the stream | ❌ Stream can contain invalid states |
| **Error feedback** | ✅ Caller gets Result<State, Error> | ❌ No error channel |
| **Undo** | ✅ Invalid commands are simply rejected | ❌ No undo in append-only store |

The conclusion is unambiguous: Event Sourcing **must** be command-driven. The Decider constraint is not optional for ES — it is the correct architectural choice.

## Consequences

### Positive

- **Domain integrity by construction** — only validated events enter the store.
- **Explicit error handling** — `Handle()` returns `Result<TState, TError>`, forcing callers to handle rejection.
- **Synchronous simplicity** — no async overhead, no observer/interpreter indirection.
- **Projections** — the same event stream supports multiple read models via different folds.
- **Time travel** — `Rebuild()` reconstructs any point in time by replaying a prefix of events.

### Negative

- **No effect execution** — the aggregate runner records effects but doesn't execute them. Effect execution is an infrastructure concern (saga, process manager).
- **In-memory store limitation** — the current `EventStore<TEvent>` is in-memory. Production use requires a persistent store (EventStoreDB, Marten, etc.) with the same `Append` + `Replay` interface.
- **Decider required** — unlike MVU and Actor which work with plain Automaton, ES mandates the Decider. This adds type parameter complexity.

### Neutral

- The ES runtime is intentionally **not** built on `AutomatonRuntime`. This is a deliberate design choice — ES's synchronous decide-then-append pattern does not benefit from the async observer/interpreter loop.
- The `DecidingRuntime` (ADR-004) exists as a separate option for contexts that want the full async Observer + Interpreter + Decide pipeline.

## References

- Young, G. (2010). "CQRS Documents." [cqrs.files.wordpress.com](https://cqrs.files.wordpress.com/2010/11/cqrs_documents.pdf).
- Chassaing, J. (2021). "Functional Event Sourcing Decider." [thinkbeforecoding.com](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider).
- Kleppmann, M. (2017). *Designing Data-Intensive Applications*. Chapter 11: Stream Processing.
- Boner, J. et al. (2014). "The Reactive Manifesto." [reactivemanifesto.org](https://www.reactivemanifesto.org/).
