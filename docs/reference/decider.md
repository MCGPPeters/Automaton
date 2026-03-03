# Decider, DecidingRuntime

`namespace Automaton`

Command validation layer for Automatons.

---

## Decider&lt;TState, TCommand, TEvent, TEffect, TError, TParameters&gt;

```csharp
public interface Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
    : Automaton<TState, TEvent, TEffect, TParameters>
{
    static abstract Result<TEvent[], TError> Decide(TState state, TCommand command);
    static virtual bool IsTerminal(TState state) => false;
}
```

A Decider is an Automaton that validates commands before producing events.

### Type Parameters

| Parameter | Description |
| --------- | ----------- |
| `TState` | The state of the automaton. |
| `TCommand` | Commands representing user intent. |
| `TEvent` | Events representing validated facts. |
| `TEffect` | Effects produced by transitions. |
| `TError` | Errors produced by invalid commands. |
| `TParameters` | The parameters required to initialize the automaton. Use `Unit` for parameterless automata. |

### Methods

#### Decide

```csharp
static abstract Result<TEvent[], TError> Decide(TState state, TCommand command);
```

Validates a command against the current state, producing events or an error.

**This function must be pure.** Its return value depends only on its inputs, and it produces no side effects. All external data (time, exchange rates, etc.) must be included in the command before calling Decide.

- `Ok(events)` — command accepted; events will be dispatched through Transition.
- `Err(error)` — command rejected; state remains unchanged.
- `Ok([])` (empty array) — "accepted but nothing happened" (idempotent command).

#### IsTerminal

```csharp
static virtual bool IsTerminal(TState state) => false;
```

Whether the automaton has reached a terminal state. Terminal states signal that no further commands should be processed.

Defaults to `false`. Override when the domain has a natural end-of-life:

```csharp
public static bool IsTerminal(OrderState state) =>
    state.Status is OrderStatus.Shipped or OrderStatus.Cancelled;
```

---

## DecidingRuntime&lt;TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters&gt;

```csharp
public sealed class DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters> : IDisposable
    where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
```

Runtime that validates commands via Decide before dispatching events through AutomatonRuntime.

### Properties

| Property | Type | Description |
| -------- | ---- | ----------- |
| `State` | `TState` | The current state. |
| `Events` | `IReadOnlyList<TEvent>` | All dispatched events (including feedback). |
| `IsTerminal` | `bool` | Whether `TDecider.IsTerminal(State)` is `true`. |

### Start

```csharp
public static async ValueTask<DecidingRuntime<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>> Start(
    TParameters parameters,
    Observer<TState, TEvent, TEffect> observer,
    Interpreter<TEffect, TEvent> interpreter,
    bool threadSafe = true,
    bool trackEvents = true,
    CancellationToken cancellationToken = default)
```

Creates and starts a deciding runtime, interpreting init effects immediately.

| Parameter | Default | Description |
| --------- | ------- | ----------- |
| `parameters` | — | Initialization parameters passed to the automaton's Init method. Use `default` for `Unit`. |
| `observer` | — | Observer called after each transition. |
| `interpreter` | — | Interpreter that converts effects to feedback events. |
| `threadSafe` | `true` | When `true`, Handle calls are serialized. |
| `trackEvents` | `true` | When `true`, all events are recorded in `Events`. |
| `cancellationToken` | — | Token to cancel the operation. |

### Handle

```csharp
public ValueTask<Result<TState, TError>> Handle(
    TCommand command,
    CancellationToken cancellationToken = default)
```

Validates and handles a command: Decide → Dispatch events → return new state or error.

**On success (`Ok`):** Each event from Decide is dispatched through the underlying AutomatonRuntime (triggering transitions, observer, and interpreter). The final state is returned.

**On error (`Err`):** No events are dispatched. State remains unchanged.

**Atomicity:** The entire Handle operation (Decide + all Dispatches) executes under a single lock acquisition. Concurrent Handle calls are serialized — no interleaving between Decide reading state and events being dispatched. This prevents TOCTOU races.

### Reset

```csharp
public void Reset(TState state)
```

Replaces the current state without triggering a transition. Used for hydration from an event store or snapshot.

> ⚠️ Do not call from within a Handle callback when `threadSafe` is `true` — it will deadlock.

### Dispose

```csharp
public void Dispose()
```

Disposes the underlying runtime's semaphore.

---

## See Also

- [The Decider](../concepts/the-decider.md) — conceptual explanation
- [Upgrading to Decider](../guides/upgrading-to-decider.md) — migration guide
- [Result](result.md) — the return type of Decide
- [Tutorial 05](../tutorials/05-command-validation.md) — full walkthrough
