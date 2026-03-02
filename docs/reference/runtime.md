# AutomatonRuntime, Observer, Interpreter

`namespace Automaton`

The shared runtime that executes the automaton loop: dispatch → transition → observe → interpret.

---

## Observer

```csharp
public delegate ValueTask Observer<in TState, in TEvent, in TEffect>(
    TState state,
    TEvent @event,
    TEffect effect);
```

Observes each transition triple `(state, event, effect)` after the automaton steps.

The observer is the extension point for side effects that depend on the transition result: rendering a view (MVU), persisting an event (ES), or logging an audit trail.

**Contract:**

- Called *after* the transition — state has already advanced.
- Should not throw. If it does, the transition is committed and the exception propagates to the caller.
- Returns `ValueTask` to avoid heap allocation for synchronous implementations (the common case).

---

## Interpreter

```csharp
public delegate ValueTask<TEvent[]> Interpreter<in TEffect, TEvent>(TEffect effect);
```

Interprets an effect by converting it into zero or more feedback events.

Feedback events are dispatched back into the automaton, creating a closed loop. Return an empty array (`[]`) for fire-and-forget effects.

**Contract:**

- Should not throw. If it does, the transition is committed and the exception propagates.
- Returns `ValueTask<TEvent[]>` to avoid heap allocation when returning synchronously.
- Feedback loops are bounded by `MaxFeedbackDepth` (64).

---

## AutomatonRuntime&lt;TAutomaton, TState, TEvent, TEffect&gt;

```csharp
public sealed class AutomatonRuntime<TAutomaton, TState, TEvent, TEffect> : IDisposable
    where TAutomaton : Automaton<TState, TEvent, TEffect>
```

The shared automaton runtime: a monadic left fold with Observer and Interpreter.

### Constants

| Constant | Value | Description |
| -------- | ----- | ----------- |
| `MaxFeedbackDepth` | `64` | Maximum recursion depth for interpreter feedback loops. |

### Properties

| Property | Type | Description |
| -------- | ---- | ----------- |
| `State` | `TState` | The current state of the automaton. |
| `Events` | `IReadOnlyList<TEvent>` | All dispatched events (including feedback). Empty list when tracking is disabled. |

### Constructor

```csharp
public AutomatonRuntime(
    TState initialState,
    Observer<TState, TEvent, TEffect> observer,
    Interpreter<TEffect, TEvent> interpreter,
    bool threadSafe = true,
    bool trackEvents = true)
```

Creates a runtime with the given initial state, observer, and interpreter. Use when you need to control initialization order (e.g., rendering an initial view before interpreting effects).

| Parameter | Default | Description |
| --------- | ------- | ----------- |
| `initialState` | — | Initial state for the automaton. |
| `observer` | — | Observer called after each transition. |
| `interpreter` | — | Interpreter that converts effects to feedback events. |
| `threadSafe` | `true` | When `true`, all public entry points are serialized via semaphore. |
| `trackEvents` | `true` | When `true`, all dispatched events are recorded in `Events`. |

**Throws:** `ArgumentNullException` if `observer` or `interpreter` is null.

### Start

```csharp
public static async ValueTask<AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>> Start(
    Observer<TState, TEvent, TEffect> observer,
    Interpreter<TEffect, TEvent> interpreter,
    bool threadSafe = true,
    bool trackEvents = true,
    CancellationToken cancellationToken = default)
```

Creates and starts a runtime, interpreting init effects immediately. This is the recommended way to create a runtime.

Calls `TAutomaton.Init()`, then interprets the init effect (which may produce feedback events and additional transitions).

### Dispatch

```csharp
public ValueTask Dispatch(TEvent @event, CancellationToken cancellationToken = default)
```

Dispatches an event through the full cycle: transition → observe → interpret effects → dispatch feedback events.

**Thread safety:** Concurrent calls are serialized via semaphore (when `threadSafe` is `true`).

**Atomicity:** The transition function is pure and cannot fail. If the observer or interpreter throws, the state has already advanced.

### InterpretEffect

```csharp
public ValueTask InterpretEffect(TEffect effect, CancellationToken cancellationToken = default)
```

Interprets an effect, dispatching any feedback events back into the loop.

**Advanced API** — use for custom runtimes that need to control initialization order. For normal usage, prefer `Dispatch` or `Start`.

### Reset

```csharp
public void Reset(TState state)
```

Replaces the current state without triggering a transition or observer.

Used by Event Sourcing to hydrate state from a replayed event stream, or by Actors for supervision/restart strategies.

> ⚠️ Do not call from within an observer or interpreter callback when `threadSafe` is `true` — it will deadlock.

### Dispose

```csharp
public void Dispose()
```

Disposes the semaphore used for thread safety.

---

## ObserverExtensions

```csharp
public static class ObserverExtensions
```

### Then

```csharp
public static Observer<TState, TEvent, TEffect> Then<TState, TEvent, TEffect>(
    this Observer<TState, TEvent, TEffect> first,
    Observer<TState, TEvent, TEffect> second)
```

Composes two observers sequentially: `first` runs, then `second`. Both see the same `(state, event, effect)` triple.

Uses async elision — when both observers complete synchronously, no async state machine is allocated.

```csharp
var combined = logger.Then(metrics).Then(persister);
```

---

## See Also

- [The Runtime](../concepts/the-runtime.md) — conceptual explanation
- [Observer Composition](../guides/observer-composition.md) — recipes for combining observers
- [Building Custom Runtimes](../guides/building-custom-runtimes.md) — how to wire your own
