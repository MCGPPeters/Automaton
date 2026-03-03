# AutomatonRuntime, Observer, Interpreter

`namespace Automaton`

The shared runtime that executes the automaton loop: dispatch → transition → observe → interpret.

---

## PipelineError

```csharp
public readonly record struct PipelineError(
    string Message,
    string? Source = null,
    Exception? Exception = null)
```

A structured error from an Observer or Interpreter pipeline stage. Errors propagate through the dispatch chain via `Result<T, PipelineError>`, giving callers structured, composable error handling.

| Property | Type | Description |
| -------- | ---- | ----------- |
| `Message` | `string` | Human-readable description of the failure. |
| `Source` | `string?` | The pipeline stage that produced the error (e.g., `"persist"`, `"render"`). |
| `Exception` | `Exception?` | The underlying exception, if the error originated from a caught exception. |

---

## Unit

```csharp
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}
```

The unit type — a type with exactly one value. Used where a success type is required but no meaningful value exists. `Result<Unit, PipelineError>` replaces `Result<void, PipelineError>` which is not expressible in C#.

---

## PipelineResult

```csharp
public static class PipelineResult
{
    public static readonly ValueTask<Result<Unit, PipelineError>> Ok;
}
```

Pre-allocated Result value for the happy path. Avoids allocating a new `Result<Unit, PipelineError>` on every observer call. Since `Result` is a readonly struct and `ValueTask` wraps it without heap allocation, this is the zero-alloc fast path.

---

## Observer

```csharp
public delegate ValueTask<Result<Unit, PipelineError>> Observer<in TState, in TEvent, in TEffect>(
    TState state,
    TEvent @event,
    TEffect effect);
```

Observes each transition triple `(state, event, effect)` after the automaton steps.

The observer is the extension point for side effects that depend on the transition result: rendering a view (MVU), persisting an event (ES), or logging an audit trail.

**Contract:**

- Called *after* the transition — state has already advanced.
- Returns `Result<Unit, PipelineError>` — errors propagate as values, not exceptions.
- Return `PipelineResult.Ok` on the happy path (zero-alloc).
- Returns `ValueTask` to avoid heap allocation for synchronous implementations (the common case).

---

## Interpreter

```csharp
public delegate ValueTask<Result<TEvent[], PipelineError>> Interpreter<in TEffect, TEvent>(TEffect effect);
```

Interprets an effect by converting it into zero or more feedback events.

Feedback events are dispatched back into the automaton, creating a closed loop. Return an empty array (`[]`) wrapped in `Result.Ok` for fire-and-forget effects.

**Contract:**

- Returns `Result<TEvent[], PipelineError>` — errors propagate as values, not exceptions.
- Returns `ValueTask<Result<...>>` to avoid heap allocation when returning synchronously.
- Feedback loops are bounded by `MaxFeedbackDepth` (64).

---

## AutomatonRuntime&lt;TAutomaton, TState, TEvent, TEffect, TParameters&gt;

```csharp
public sealed class AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters> : IDisposable
    where TAutomaton : Automaton<TState, TEvent, TEffect, TParameters>
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
public static async ValueTask<AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>> Start(
    TParameters parameters,
    Observer<TState, TEvent, TEffect> observer,
    Interpreter<TEffect, TEvent> interpreter,
    bool threadSafe = true,
    bool trackEvents = true,
    CancellationToken cancellationToken = default)
```

Creates and starts a runtime, interpreting initial effects immediately. This is the recommended way to create a runtime.

Calls `TAutomaton.Initialize(parameters)`, then interprets the initial effects (which may produce feedback events and additional transitions).

### Dispatch

```csharp
public ValueTask<Result<Unit, PipelineError>> Dispatch(
    TEvent @event,
    CancellationToken cancellationToken = default)
```

Dispatches an event through the full cycle: transition → observe → interpret effects → dispatch feedback events.

Returns `Result<Unit, PipelineError>` — `Ok` on success, `Err(PipelineError)` if any pipeline stage fails.

**Thread safety:** Concurrent calls are serialized via semaphore (when `threadSafe` is `true`).

**Atomicity:** The transition function is pure and cannot fail. If an observer or interpreter returns `Err`, the state has already advanced but the error is propagated to the caller.

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

### Then (Kleisli Composition)

```csharp
public static Observer<TState, TEvent, TEffect> Then<TState, TEvent, TEffect>(
    this Observer<TState, TEvent, TEffect> first,
    Observer<TState, TEvent, TEffect> second)
```

Composes two observers sequentially: `first` runs, then `second` if `first` succeeds. Short-circuits on `Err` — if `first` returns an error, `second` is never called.

This is Kleisli composition (`>=>`) for `Result<Unit, PipelineError>`: the monadic bind that composes effectful functions while preserving short-circuit semantics.

Uses async elision — when both observers complete synchronously, no async state machine is allocated.

```csharp
var pipeline = logger.Then(metrics).Then(persister);
```

### Where (Guard)

```csharp
public static Observer<TState, TEvent, TEffect> Where<TState, TEvent, TEffect>(
    this Observer<TState, TEvent, TEffect> observer,
    Func<TState, TEvent, TEffect, bool> predicate)
```

Guards an observer with a predicate. The observer only runs when the predicate returns `true`; otherwise returns `Ok` immediately.

```csharp
var errorOnly = logger.Where((state, evt, eff) => evt is ErrorOccurred);
```

### Select (Contramap)

```csharp
public static Observer<TState2, TEvent2, TEffect2> Select<TState, TEvent, TEffect, TState2, TEvent2, TEffect2>(
    this Observer<TState, TEvent, TEffect> observer,
    Func<TState2, TState> mapState,
    Func<TEvent2, TEvent> mapEvent,
    Func<TEffect2, TEffect> mapEffect)
```

Contramaps an observer's inputs, adapting it from one type to another. This is a contravariant functor operation — it transforms the *inputs* rather than the output.

```csharp
Observer<AppState, AppEvent, AppEffect> appObserver =
    domainLogger.Select(
        (AppState s) => s.Domain,
        (AppEvent e) => e.Inner,
        (AppEffect eff) => eff.Inner);
```

### Catch (Error Recovery)

```csharp
public static Observer<TState, TEvent, TEffect> Catch<TState, TEvent, TEffect>(
    this Observer<TState, TEvent, TEffect> observer,
    Func<PipelineError, Result<Unit, PipelineError>> handler)
```

Attaches an error handler. When the observer returns `Err`, the handler runs and can either recover (return `Ok`) or transform the error (return a new `Err`).

```csharp
var resilient = persister.Catch(err =>
{
    log.Warning("Persist failed: {Message}", err.Message);
    return Result<Unit, PipelineError>.Ok(Unit.Value); // swallow
});
```

### Combine (Applicative)

```csharp
public static Observer<TState, TEvent, TEffect> Combine<TState, TEvent, TEffect>(
    this Observer<TState, TEvent, TEffect> first,
    Observer<TState, TEvent, TEffect> second)
```

Runs two observers sequentially but does **not** short-circuit — the second observer always runs, even if the first returns `Err`. Returns the first error encountered, or `Ok` if both succeed.

Contrast with `Then` (short-circuit) — `Combine` is useful when both side effects must run regardless of individual failures.

```csharp
var both = persister.Combine(notifier); // notifier runs even if persister fails
```

---

## InterpreterExtensions

```csharp
public static class InterpreterExtensions
```

### Then (Kleisli Composition)

```csharp
public static Interpreter<TEffect, TEvent> Then<TEffect, TEvent>(
    this Interpreter<TEffect, TEvent> first,
    Interpreter<TEffect, TEvent> second)
```

Composes two interpreters sequentially. Both run on the same effect; their result events are concatenated. Short-circuits on `Err`.

Uses `ConcatEvents` optimization — avoids allocation when one side returns an empty array.

```csharp
var interpreter = localHandler.Then(remoteSync);
```

### Where (Guard)

```csharp
public static Interpreter<TEffect, TEvent> Where<TEffect, TEvent>(
    this Interpreter<TEffect, TEvent> interpreter,
    Func<TEffect, bool> predicate)
```

Guards an interpreter with a predicate. Returns `Ok([])` when the predicate is `false`.

### Select (Contramap)

```csharp
public static Interpreter<TEffect2, TEvent> Select<TEffect, TEvent, TEffect2>(
    this Interpreter<TEffect, TEvent> interpreter,
    Func<TEffect2, TEffect> mapEffect)
```

Contramaps an interpreter's input type.

### Catch (Error Recovery)

```csharp
public static Interpreter<TEffect, TEvent> Catch<TEffect, TEvent>(
    this Interpreter<TEffect, TEvent> interpreter,
    Func<PipelineError, Result<TEvent[], PipelineError>> handler)
```

Attaches an error handler. When the interpreter returns `Err`, the handler can recover with events or propagate a different error.

---

## See Also

- [The Runtime](../concepts/the-runtime.md) — conceptual explanation
- [Observer Composition](../guides/observer-composition.md) — recipes for combining observers
- [Building Custom Runtimes](../guides/building-custom-runtimes.md) — how to wire your own
