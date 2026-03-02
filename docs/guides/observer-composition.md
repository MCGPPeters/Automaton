# Observer Composition

How to chain, filter, and combine observers for complex observation pipelines.

## Basic Composition with `Then`

`Then` runs two observers sequentially — the first completes, then the second runs. Both see the same `(state, event, effect)` triple:

```csharp
var combined = logger.Then(metrics);
```

### Chaining Multiple Observers

`Then` is associative — you can chain any number of observers:

```csharp
var pipeline = logger
    .Then(persister)
    .Then(metrics)
    .Then(alerter);
```

All four run in order for every transition. If any observer throws, subsequent observers don't run and the exception propagates.

## Common Observer Patterns

### Logging Observer

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> logger =
    (state, @event, effect) =>
    {
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] " +
            $"{@event.GetType().Name} → {state} | {effect.GetType().Name}");
        return ValueTask.CompletedTask;
    };
```

### Capture Observer (for testing)

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> Capture(
    List<(ThermostatState, ThermostatEvent, ThermostatEffect)> log) =>
    (state, @event, effect) =>
    {
        log.Add((state, @event, effect));
        return ValueTask.CompletedTask;
    };
```

### Filtering Observer

Only observe specific event or effect types:

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> alertsOnly =
    (state, @event, effect) =>
    {
        if (effect is ThermostatEffect.SendNotification(var message))
            Console.WriteLine($"🚨 ALERT: {message}");
        return ValueTask.CompletedTask;
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
        return ValueTask.CompletedTask;
    };
```

### Async Observer

Observers return `ValueTask`, so async operations work naturally:

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> persister =
    async (state, @event, effect) =>
    {
        await database.SaveEventAsync(@event);
    };
```

## Building a Pipeline

Combine all patterns into a production pipeline:

```csharp
var pipeline = Capture(auditLog)        // record for auditing
    .Then(logger)                        // write to console/structured log
    .Then(metrics)                       // update counters
    .Then(alertsOnly)                    // fire alerts
    .Then(persister);                    // persist to database

var runtime = await AutomatonRuntime<Thermostat, ThermostatState,
    ThermostatEvent, ThermostatEffect>.Start(pipeline, interpreter);
```

## Performance Considerations

- `Then` uses async elision: when both observers complete synchronously (the common case for in-memory observers), no async state machine is allocated.
- Each `Then` adds one function call per transition. For hot paths with many observers, consider combining logic into a single observer instead.
- Observers run *after* the transition — state has already advanced. A slow observer doesn't block the transition itself, but it does block subsequent dispatches (they're serialized).

## See Also

- [The Runtime](../concepts/the-runtime.md) — how observers fit into the dispatch cycle
- [API Reference: Runtime](../reference/runtime.md) — `ObserverExtensions.Then` documentation
- [Testing Strategies](testing-strategies.md) — capture observers for test assertions
