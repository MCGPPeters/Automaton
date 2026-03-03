# Observer Composition

How to chain, filter, and combine observers using monadic combinators.

## Overview

Observers return `Result<Unit, PipelineError>` — errors propagate as values, not exceptions. This enables a rich set of FP-style combinators:

| Combinator | Algebra | Behavior |
| ---------- | ------- | -------- |
| `Then` | Kleisli composition | Sequential, short-circuits on `Err` |
| `Where` | Guard | Skips observer when predicate is `false` |
| `Select` | Contramap | Adapts observer from one type to another |
| `Catch` | Error handler | Recovers from or transforms errors |
| `Combine` | Applicative | Both run regardless, returns first error |

## Basic Composition with `Then`

`Then` runs two observers sequentially — the first completes, then the second runs *if the first succeeds*. Both see the same `(state, event, effect)` triple:

```csharp
var combined = logger.Then(metrics);
```

### Short-Circuit Semantics

`Then` is Kleisli composition (`>=>`) — if any observer returns `Err`, subsequent observers are skipped:

```csharp
var pipeline = logger
    .Then(persister)    // if persister returns Err...
    .Then(metrics)      // ...metrics never runs
    .Then(alerter);     // ...alerter never runs
```

The error propagates to the `Dispatch` caller as `Result.Err(PipelineError)`.

## Filtering with `Where`

`Where` guards an observer with a predicate. When the predicate returns `false`, the observer is skipped and `Ok` is returned immediately:

```csharp
var alertsOnly = notifier.Where(
    (state, evt, eff) => eff is ThermostatEffect.SendNotification);

var errorsOnly = logger.Where(
    (state, evt, eff) => evt is ErrorOccurred);
```

## Error Recovery with `Catch`

`Catch` attaches an error handler that can recover from failures or transform errors:

```csharp
// Swallow persist errors (log and continue)
var resilient = persister.Catch(err =>
{
    log.Warning("Persist failed: {Message}", err.Message);
    return Result<Unit, PipelineError>.Ok(Unit.Value);
});

// Transform the error
var tagged = persister.Catch(err =>
    Result<Unit, PipelineError>.Err(err with { Source = "persistence-layer" }));
```

## Running Both with `Combine`

`Combine` runs two observers sequentially but does **not** short-circuit — the second observer always runs, even if the first returns `Err`. Returns the first error encountered:

```csharp
// Notifier always runs, even if persister fails
var both = persister.Combine(notifier);
```

Contrast with `Then`: use `Then` when the second observer depends on the first succeeding. Use `Combine` when both side effects must run regardless.

## Type Adaptation with `Select`

`Select` contramaps an observer's inputs, adapting it from one domain type to another:

```csharp
Observer<AppState, AppEvent, AppEffect> appObserver =
    domainLogger.Select(
        (AppState s) => s.Domain,
        (AppEvent e) => e.Inner,
        (AppEffect eff) => eff.Inner);
```

## Common Observer Patterns

### Logging Observer

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> logger =
    (state, @event, effect) =>
    {
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] " +
            $"{@event.GetType().Name} → {state} | {effect.GetType().Name}");
        return PipelineResult.Ok;
    };
```

### Capture Observer (for testing)

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> Capture(
    List<(ThermostatState, ThermostatEvent, ThermostatEffect)> log) =>
    (state, @event, effect) =>
    {
        log.Add((state, @event, effect));
        return PipelineResult.Ok;
    };
```

### Metrics Observer

Track operational metrics without coupling to a specific metrics library:

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> metrics =
    (state, @event, effect) =>
    {
        switch (@event)
        {
            case ThermostatEvent.HeaterTurnedOn:
                heaterOnCounter.Increment();
                break;
            case ThermostatEvent.HeaterTurnedOff:
                heaterOffCounter.Increment();
                break;
            case ThermostatEvent.AlertRaised:
                alertCounter.Increment();
                break;
        }
        return PipelineResult.Ok;
    };
```

### Async Observer

Observers return `ValueTask<Result<Unit, PipelineError>>`, so async operations work naturally:

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> persister =
    async (state, @event, effect) =>
    {
        await database.SaveEventAsync(@event);
        return Result<Unit, PipelineError>.Ok(Unit.Value);
    };
```

## Building a Pipeline

Combine all patterns into a production pipeline:

```csharp
var pipeline = Capture(auditLog)             // record for auditing
    .Then(logger)                             // write to console/structured log
    .Then(metrics)                            // update counters
    .Then(notifier                            // fire alerts
        .Where((s, e, eff) =>                 //   ...only for alert effects
            eff is ThermostatEffect.SendNotification))
    .Then(persister                           // persist to database
        .Catch(err =>                         //   ...recover from DB errors
        {
            log.Warning("Persist: {Msg}", err.Message);
            return Result<Unit, PipelineError>.Ok(Unit.Value);
        }));

var runtime = await AutomatonRuntime<Thermostat, ThermostatState,
    ThermostatEvent, ThermostatEffect, Unit>.Start(default, pipeline, interpreter);
```

## Interpreter Composition

Interpreters have the same combinators (`Then`, `Where`, `Select`, `Catch`):

```csharp
Interpreter<CounterEffect, CounterEvent> local =
    effect => new ValueTask<Result<CounterEvent[], PipelineError>>(
        Result<CounterEvent[], PipelineError>.Ok([]));

Interpreter<CounterEffect, CounterEvent> remote =
    async effect =>
    {
        await api.SyncAsync(effect);
        return Result<CounterEvent[], PipelineError>.Ok([]);
    };

var interpreter = local.Then(remote);
```

`Then` for interpreters concatenates the result events from both sides, using `ConcatEvents` optimization to avoid allocation when one side returns an empty array.

## Performance Considerations

- `Then` uses async elision: when both observers complete synchronously (the common case for in-memory observers), no async state machine is allocated.
- Each combinator adds one function call per transition. For hot paths with many observers, consider combining logic into a single observer.
- Observers run *after* the transition — state has already advanced. A slow observer doesn't block the transition itself, but it does block subsequent dispatches (they're serialized).
- `PipelineResult.Ok` is pre-allocated — use it for the fast path instead of constructing `Result<Unit, PipelineError>.Ok(Unit.Value)`.

## See Also

- [The Runtime](../concepts/the-runtime.md) — how observers fit into the dispatch cycle
- [API Reference: Runtime](../reference/runtime.md) — `ObserverExtensions` and `InterpreterExtensions` documentation
- [Testing Strategies](testing-strategies.md) — capture observers for test assertions
