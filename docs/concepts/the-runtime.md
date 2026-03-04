# The Runtime

The kernel defines a pure transition function. The **runtime** executes it in a loop — dispatching events, observing transitions, interpreting effects, and feeding results back in.

## The Loop

Every dispatch follows this cycle:

```text
Event
  → Transition(state, event) → (state', effect)
    → Observer(state', event, effect)
    → Interpreter(effect) → feedback events[]
      → Dispatch each feedback event (recurse)
```

One call to `Dispatch` can trigger multiple transitions if the interpreter returns feedback events.

## Two Extension Points

The runtime is parameterized by exactly two callbacks. Every specialized runtime (MVU, Event Sourcing, Actor) is just a specific choice of these two:

### Observer — "what do you want to see?"

```csharp
public delegate ValueTask<Result<Unit, PipelineError>> Observer<in TState, in TEvent, in TEffect>(
    TState state,
    TEvent @event,
    TEffect effect);
```

The observer sees every transition triple `(state, event, effect)` after it happens. Use it to:

- **Render a view** (MVU)
- **Persist an event** (Event Sourcing)
- **Log an audit trail** (any runtime)
- **Update metrics** (any runtime)

The observer is called *after* the transition — state has already advanced. Returns `Result<Unit, PipelineError>` — errors propagate as values, not exceptions. Return `PipelineResult.Ok` on the happy path (zero-alloc).

### Interpreter — "what should happen next?"

```csharp
public delegate ValueTask<Result<TEvent[], PipelineError>> Interpreter<in TEffect, TEvent>(TEffect effect);
```

The interpreter converts an effect into zero or more **feedback events** that are dispatched back into the automaton:

- Return `Result<TEvent[], PipelineError>.Ok([])` for fire-and-forget effects
- Return `Result<TEvent[], PipelineError>.Ok([event1, event2])` to feed events back into the loop
- Return `Result<TEvent[], PipelineError>.Err(...)` to signal an error

This creates a **closed feedback loop**: effects can trigger more transitions, which produce more effects, which trigger more transitions...

## The Monadic Left Fold

Mathematically, the runtime is a [monadic left fold](https://en.wikipedia.org/wiki/Fold_(higher-order_function)) over an event stream:

```text
foldM : (State → Event → M (State, Effect)) → State → [Event] → M State
```

The `M` is the effect monad — `ValueTask` in this case. The fold processes events sequentially, threading state through each transition.

This is the same operation that:
- **Event Sourcing** uses to rebuild state from stored events (`events.Aggregate(seed, transition)`)
- **MVU** uses to process a stream of UI messages
- **Actors** use to process a stream of mailbox messages

It's all the same fold. The runtime just wires it differently.

## Creating a Runtime

### Using `Start` (recommended)

`Start` creates the runtime, calls `Initialize()`, and interprets the initial effects:

```csharp
var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect, Unit>
    .Start(default, observer, interpreter);
```

### Using the Constructor (advanced)

The constructor lets you control initialization order — useful when you need to render an initial view before interpreting effects:

```csharp
var (state, effect) = Counter.Initialize(default);

// Render the initial state before any effects run
views.Add(render(state));

var runtime = new AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect, Unit>(
    state, observer, interpreter);

// Now interpret the initial effects
await runtime.InterpretEffect(effect);
```

## Dispatch

`Dispatch` is the primary entry point. It runs the full cycle: transition → observe → interpret → recurse:

```csharp
var result = await runtime.Dispatch(new CounterEvent.Increment());
```

Returns `Result<Unit, PipelineError>` — `Ok` on success, `Err(PipelineError)` if any pipeline stage fails.

After `Dispatch` returns, `runtime.State` reflects all transitions — including those triggered by feedback events.

## Production Guarantees

The runtime provides four guarantees out of the box:

### Thread Safety

All public mutating methods are serialized via a `SemaphoreSlim`. Concurrent callers are queued, never interleaved:

```csharp
// Safe from multiple threads — dispatches are serialized
await Task.WhenAll(
    runtime.Dispatch(new CounterEvent.Increment()),
    runtime.Dispatch(new CounterEvent.Increment()),
    runtime.Dispatch(new CounterEvent.Increment()));
```

For single-threaded scenarios (actors, UI loops, benchmarks), pass `threadSafe: false` to eliminate semaphore overhead:

```csharp
var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect, Unit>
    .Start(default, observer, interpreter, threadSafe: false);
```

### Cancellation

All async methods accept `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await runtime.Dispatch(new CounterEvent.Increment(), cts.Token);
```

### Feedback Depth Guard

Interpreter feedback loops are bounded at 64 depth. If an effect always produces events whose transitions produce the same effect, the runtime throws `InvalidOperationException` instead of stack-overflowing:

```text
Effect → Event → Transition → same Effect → Event → ... → depth 64 → throw
```

### Event Tracking

By default, all dispatched events (including feedback events) are recorded in `runtime.Events`. Disable for hot paths:

```csharp
var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect, Unit>
    .Start(default, observer, interpreter, trackEvents: false);
```

## Observer Composition

Observers compose with **monadic combinators** that form a pipeline:

### Then (Kleisli Composition)

Sequential composition with short-circuit on error:

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> logger =
    (state, @event, effect) =>
    {
        Console.WriteLine($"[LOG] {@event.GetType().Name} → {state}");
        return PipelineResult.Ok;
    };

Observer<ThermostatState, ThermostatEvent, ThermostatEffect> metrics =
    (state, @event, effect) =>
    {
        // record metrics
        return PipelineResult.Ok;
    };

var pipeline = logger.Then(metrics);
```

When `pipeline` is called, `logger` runs first. If it succeeds (`Ok`), `metrics` runs. If `logger` returns `Err`, `metrics` is skipped and the error propagates.

### Where (Guard)

Filter which transitions an observer processes:

```csharp
var errorOnly = logger.Where((state, evt, eff) => evt is ErrorOccurred);
```

### Catch (Error Recovery)

Handle errors from an observer:

```csharp
var resilient = persister.Catch(err =>
{
    log.Warning("Persist failed: {Message}", err.Message);
    return Result<Unit, PipelineError>.Ok(Unit.Value); // swallow error
});
```

### Combine (Applicative)

Run both observers regardless of individual failures:

```csharp
var both = persister.Combine(notifier); // notifier runs even if persister fails
```

See the [Observer Composition guide](../guides/observer-composition.md) for advanced recipes.

## Reset

`Reset` replaces the current state without triggering a transition or observer. Used by:

- **Event Sourcing** — hydrate state from a replayed event stream
- **Actors** — supervision/restart strategies

```csharp
runtime.Reset(new CounterState(42));
Console.WriteLine(runtime.State.Count); // 42
```

> ⚠️ Do not call `Reset` from within an observer or interpreter callback when `threadSafe` is `true` — it will deadlock (the gate is already held).

## How Runtimes Are Built

Every specialized runtime in the library is just `AutomatonRuntime` with specific Observer and Interpreter wiring:

| Runtime | Observer | Interpreter |
| ------- | -------- | ----------- |
| **MVU** | Render the new state to a view | Execute effects, return feedback events |
| **Event Sourcing** | Append event to store | No-op (empty) |
| **Actor** | No-op (state is internal) | Execute effect with self-reference |

See [Runtimes Compared](runtimes-compared.md) for a detailed comparison, [Building Custom Runtimes](../guides/building-custom-runtimes.md) to create your own, or [Composition](composition.md) to combine multiple automata into a single runtime.

## Next

- [**The Decider**](the-decider.md) — adding command validation before transitions
- [**Composition**](composition.md) — combining multiple automata into one runtime
- [**Runtimes Compared**](runtimes-compared.md) — choosing the right runtime pattern
- [**API Reference: Runtime**](../reference/runtime.md) — complete method documentation
