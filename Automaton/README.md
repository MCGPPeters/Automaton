# Automaton

**Write once, run everywhere.**

A minimal, production-hardened Mealy machine kernel for building state machines, MVU runtimes, event-sourced aggregates, and actor systems — based on the observation that all three are instances of the same mathematical structure: a **Mealy machine** (finite-state transducer with effects).

```text
transition : (State × Event) → (State × Effect)
```

Define your pure domain logic once as a transition function. Then plug it into any runtime — a browser UI loop, an event-sourced aggregate, or a mailbox actor — without changing a single line.

## Installation

```bash
dotnet add package Automaton
```

## The Kernel

```csharp
public interface Automaton<TState, TEvent, TEffect, TParameters>
{
    static abstract (TState State, TEffect Effect) Initialize(TParameters parameters);
    static abstract (TState State, TEffect Effect) Transition(TState state, TEvent @event);
}
```

Two methods. Zero dependencies. The rest is runtime. Use `Unit` as `TParameters` for automata that require no initialization parameters.

## Example: Counter

```csharp
public record CounterState(int Count);

public interface CounterEvent
{
    record struct Increment : CounterEvent;
    record struct Decrement : CounterEvent;
}

public interface CounterEffect
{
    record struct None : CounterEffect;
}

public class Counter : Automaton<CounterState, CounterEvent, CounterEffect, Unit>
{
    public static (CounterState, CounterEffect) Initialize(Unit _) =>
        (new CounterState(0), new CounterEffect.None());

    public static (CounterState, CounterEffect) Transition(CounterState state, CounterEvent @event) =>
        @event switch
        {
            CounterEvent.Increment => (state with { Count = state.Count + 1 }, new CounterEffect.None()),
            CounterEvent.Decrement => (state with { Count = state.Count - 1 }, new CounterEffect.None()),
            _ => throw new UnreachableException()
        };
}
```

## The Shared Runtime

```csharp
var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect, Unit>
    .Start(
        default,
        observer: (state, @event, effect) =>
        {
            Console.WriteLine($"{@event} → {state}");
            return PipelineResult.Ok;
        },
        interpreter: _ => new ValueTask<Result<CounterEvent[], PipelineError>>(
            Result<CounterEvent[], PipelineError>.Ok([])));

await runtime.Dispatch(new CounterEvent.Increment());
// Prints: Increment → CounterState { Count = 1 }
```

### Production Guarantees

| Property | Guarantee |
| -------- | --------- |
| **Thread safety** | All public mutating methods are serialized via `SemaphoreSlim`. Concurrent callers are queued, never interleaved. Pass `threadSafe: false` for single-threaded scenarios (actors, UI loops). |
| **Cancellation** | All async methods accept `CancellationToken`. |
| **Feedback depth** | Interpreter feedback loops are bounded (max 64 depth). Runaway cycles throw `InvalidOperationException`. |
| **Error propagation** | Observer and Interpreter return `Result<T, PipelineError>` — errors propagate as values, not exceptions. Use `PipelineResult.Ok` for the zero-alloc happy path. |

### Observer Composition

Observers compose with monadic combinators:

```csharp
var pipeline = renderObserver
    .Then(logObserver)              // sequential, short-circuits on Err
    .Then(metricsObserver)
    .Where((s, e, eff) =>           // guard with predicate
        eff is not CounterEffect.None)
    .Catch(err =>                   // recover from errors
        Result<Unit, PipelineError>.Ok(Unit.Value));
```

| Combinator | Behavior |
| ---------- | -------- |
| `Then` | Sequential composition, short-circuits on `Err` (Kleisli) |
| `Where` | Guard — skips observer when predicate is `false` |
| `Select` | Contramap — adapts observer from one type to another |
| `Catch` | Error recovery — handle or transform errors |
| `Combine` | Both run regardless of individual failures (applicative) |

### Building Custom Runtimes

| Runtime Pattern | Observer | Interpreter |
| --------------- | -------- | ----------- |
| **MVU** | Render the new state | Execute effects, return feedback events |
| **Event Sourcing** | Append event to store + record effect | No-op (empty) |
| **Actor** | No-op (state is internal) | Execute effect with self-reference |

See the [test project](https://github.com/MCGPPeters/Automaton/tree/main/Automaton.Tests) for complete reference implementations.

## The Decider — Command Validation

```csharp
public interface Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
    : Automaton<TState, TEvent, TEffect, TParameters>
{
    static abstract Result<TEvent[], TError> Decide(TState state, TCommand command);
    static virtual bool IsTerminal(TState state) => false;
}
```

```csharp
var runtime = await DecidingRuntime<Counter, CounterState, CounterCommand,
    CounterEvent, CounterEffect, CounterError, Unit>.Start(default, observer, interpreter);

var result = await runtime.Handle(new CounterCommand.Add(5));
// result is Ok(CounterState { Count = 5 })

var overflow = await runtime.Handle(new CounterCommand.Add(200));
// overflow is Err(CounterError.Overflow { ... }) — state unchanged
```

## Observability — OpenTelemetry Tracing

Zero-dependency tracing via `System.Diagnostics.ActivitySource`, compatible with any OpenTelemetry collector.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(AutomatonDiagnostics.SourceName));
```

When no listener is registered, instrumentation has near-zero overhead.

| Span Name | Tags |
| --------- | ---- |
| `Automaton.Start` | `automaton.type`, `automaton.state.type` |
| `Automaton.Dispatch` | `automaton.type`, `automaton.event.type` |
| `Automaton.InterpretEffect` | `automaton.type`, `automaton.effect.type` |
| `Automaton.Decider.Start` | `automaton.type`, `automaton.state.type` |
| `Automaton.Decider.Handle` | `automaton.type`, `automaton.command.type`, `automaton.result`, `automaton.error.type` |

## What's in the Box

| Type | Purpose |
| ---- | ------- |
| `Automaton<TState, TEvent, TEffect, TParameters>` | Mealy machine interface (Initialize + Transition) |
| `AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>` | Thread-safe async runtime |
| `Observer<TState, TEvent, TEffect>` | Transition observer delegate |
| `Interpreter<TEffect, TEvent>` | Effect interpreter delegate |
| `ObserverExtensions` | `Then`, `Where`, `Select`, `Catch`, `Combine` |
| `InterpreterExtensions` | `Then`, `Where`, `Select`, `Catch` |
| `Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>` | Command validation interface |
| `DecidingRuntime<...>` | Command-validating runtime wrapper |
| `Result<TSuccess, TError>` | Discriminated union with Map, Bind, MapError, LINQ query syntax |
| `Unit` | Unit type for parameterless automata |
| `PipelineError` | Structured error from Observer/Interpreter pipeline stages |
| `PipelineResult` | Pre-allocated `Ok` value for the zero-alloc happy path |
| `AutomatonDiagnostics` | OpenTelemetry-compatible tracing (ActivitySource) |

## License

Apache 2.0
