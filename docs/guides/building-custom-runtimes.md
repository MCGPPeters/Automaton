# Building Custom Runtimes

How to build your own runtime by wiring Observer and Interpreter on top of `AutomatonRuntime`.

## The Recipe

Every runtime in the library follows the same pattern:

1. Create a class that wraps `AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>`
2. Choose an Observer — what happens after each transition?
3. Choose an Interpreter — what do effects produce?
4. Expose a domain-specific API (`Dispatch`, `Handle`, `Tell`, `Render`, etc.)

That's it. Thread safety, cancellation, feedback depth, and tracing are inherited for free.

## Reference: How the Built-In Runtimes Work

| Runtime | Observer | Interpreter | API |
| ------- | -------- | ----------- | --- |
| **MVU** | Render state → view, append to view history | Execute effect → feedback events | `Dispatch(event)`, `Views` |
| **Event Sourcing** | Append event to store, record effect | No-op (empty array) | `Handle(command)`, `Store`, `Rebuild()` |
| **Actor** | No-op | Execute effect with self-reference | `Tell(message)`, `DrainMailbox()` |

## Example: Building a Logging Runtime

A minimal runtime that logs every transition to a list:

```csharp
public sealed class LoggingRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>
    where TAutomaton : Automaton<TState, TEvent, TEffect, TParameters>
{
    private readonly AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters> _core;
    private readonly List<string> _log;

    public TState State => _core.State;
    public IReadOnlyList<string> Log => _log;

    private LoggingRuntime(
        AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters> core,
        List<string> log)
    {
        _core = core;
        _log = log;
    }

    public static async ValueTask<LoggingRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>> Start(
        TParameters parameters,
        Interpreter<TEffect, TEvent> interpreter)
    {
        var log = new List<string>();

        Observer<TState, TEvent, TEffect> observer = (state, @event, effect) =>
        {
            log.Add($"[{DateTimeOffset.UtcNow:O}] {@event.GetType().Name} → {state}");
            return PipelineResult.Ok;
        };

        var core = await AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>
            .Start(parameters, observer, interpreter);

        return new LoggingRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>(core, log);
    }

    public ValueTask<Result<Unit, PipelineError>> Dispatch(TEvent @event, CancellationToken ct = default) =>
        _core.Dispatch(@event, ct);
}
```

Usage:

```csharp
var runtime = await LoggingRuntime<Counter, CounterState, CounterEvent, CounterEffect, Unit>
    .Start(default, _ => new ValueTask<Result<CounterEvent[], PipelineError>>(
        Result<CounterEvent[], PipelineError>.Ok([])));

await runtime.Dispatch(new CounterEvent.Increment());

Console.WriteLine(runtime.Log[0]);
// [2025-01-01T12:00:00Z] Increment → CounterState { Count = 1 }
```

## Example: Building a Snapshot Runtime

A runtime that periodically snapshots state:

```csharp
public sealed class SnapshotRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>
    where TAutomaton : Automaton<TState, TEvent, TEffect, TParameters>
{
    private readonly AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters> _core;
    private readonly List<(long Version, TState State)> _snapshots = [];
    private long _version;
    private readonly int _snapshotInterval;

    public TState State => _core.State;
    public IReadOnlyList<(long Version, TState State)> Snapshots => _snapshots;

    private SnapshotRuntime(
        AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters> core,
        int snapshotInterval)
    {
        _core = core;
        _snapshotInterval = snapshotInterval;
    }

    public static async ValueTask<SnapshotRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>> Start(
        TParameters parameters,
        Interpreter<TEffect, TEvent> interpreter,
        int snapshotInterval = 100)
    {
        var runtime = new SnapshotRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>(
            null!, snapshotInterval);

        Observer<TState, TEvent, TEffect> observer = (state, _, _) =>
        {
            runtime._version++;
            if (runtime._version % runtime._snapshotInterval == 0)
                runtime._snapshots.Add((runtime._version, state));
            return PipelineResult.Ok;
        };

        var (state, effect) = TAutomaton.Init(parameters);
        var core = new AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>(
            state, observer, interpreter);
        await core.InterpretEffect(effect);

        // Use reflection-free field setting via a factory pattern instead
        return new SnapshotRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>(core, snapshotInterval);
    }

    public ValueTask<Result<Unit, PipelineError>> Dispatch(TEvent @event, CancellationToken ct = default) =>
        _core.Dispatch(@event, ct);
}
```

## Design Guidelines

### Use the Constructor When You Need Control

The `Start` factory method calls `Init()` and interprets the init effect immediately. If you need to do something between initialization and effect interpretation (like the MVU runtime rendering the initial view), use the constructor:

```csharp
var (state, effect) = TAutomaton.Init(parameters);

// Do something with the initial state first
initialView = render(state);

var core = new AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>(
    state, observer, interpreter);

// Now interpret init effects
await core.InterpretEffect(effect);
```

### Choose Threading Mode

- Pass `threadSafe: true` (default) for runtimes accessed from multiple threads
- Pass `threadSafe: false` for single-threaded runtimes (actors, UI loops) to eliminate semaphore overhead

### Choose Event Tracking

- Pass `trackEvents: true` (default) if you need `runtime.Events` for history or testing
- Pass `trackEvents: false` for hot paths where event list allocation matters

### Compose Observers, Don't Multiply Runtimes

If you need logging + metrics + persistence, compose them into one observer with `Then` rather than building separate runtimes. One runtime, one transition, multiple observers.

## See Also

- [The Runtime](../concepts/the-runtime.md) — how Observer and Interpreter fit together
- [Observer Composition](observer-composition.md) — combining observers
- [Runtimes Compared](../concepts/runtimes-compared.md) — how the built-in runtimes are wired
- [API Reference: Runtime](../reference/runtime.md) — complete AutomatonRuntime documentation
