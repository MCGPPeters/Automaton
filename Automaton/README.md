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
public interface Automaton<TState, TEvent, TEffect>
{
    static abstract (TState State, TEffect Effect) Init();
    static abstract (TState State, TEffect Effect) Transition(TState state, TEvent @event);
}
```

Two methods. Zero dependencies. The rest is runtime.

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

public class Counter : Automaton<CounterState, CounterEvent, CounterEffect>
{
    public static (CounterState, CounterEffect) Init() =>
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
var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>
    .Start(
        observer: (state, @event, effect) =>
        {
            Console.WriteLine($"{@event} → {state}");
            return ValueTask.CompletedTask;
        },
        interpreter: _ => new ValueTask<CounterEvent[]>([]));

await runtime.Dispatch(new CounterEvent.Increment());
// Prints: Increment → CounterState { Count = 1 }
```

### Production Guarantees

| Property | Guarantee |
| -------- | --------- |
| **Thread safety** | All public mutating methods are serialized via `SemaphoreSlim`. Concurrent callers are queued, never interleaved. |
| **Cancellation** | All async methods accept `CancellationToken`. |
| **Feedback depth** | Interpreter feedback loops are bounded (max 64 depth). Runaway cycles throw `InvalidOperationException`. |
| **Null safety** | Observer and Interpreter are validated at construction. |

### Observer Composition

Observers compose sequentially with `Then`:

```csharp
var combined = renderObserver.Then(logObserver).Then(metricsObserver);
```

### Building Custom Runtimes

| Runtime Pattern | Observer | Interpreter |
| --------------- | -------- | ----------- |
| **MVU** | Render the new state | Execute effects, return feedback events |
| **Event Sourcing** | Append event to store + record effect | No-op (empty) |
| **Actor** | No-op (state is internal) | Execute effect with self-reference |

See the [test project](https://github.com/MCGPPeters/Automaton/tree/main/Automaton.Tests) for complete reference implementations.

## The Decider — Command Validation

```csharp
public interface Decider<TState, TCommand, TEvent, TEffect, TError>
    : Automaton<TState, TEvent, TEffect>
{
    static abstract Result<TEvent[], TError> Decide(TState state, TCommand command);
    static virtual bool IsTerminal(TState state) => false;
}
```

```csharp
var runtime = await DecidingRuntime<Counter, CounterState, CounterCommand,
    CounterEvent, CounterEffect, CounterError>.Start(observer, interpreter);

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
| `Automaton<TState, TEvent, TEffect>` | Mealy machine interface (Init + Transition) |
| `AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>` | Thread-safe async runtime |
| `Observer<TState, TEvent, TEffect>` | Transition observer delegate |
| `Interpreter<TEffect, TEvent>` | Effect interpreter delegate |
| `ObserverExtensions.Then` | Sequential observer composition |
| `Decider<TState, TCommand, TEvent, TEffect, TError>` | Command validation interface |
| `DecidingRuntime<...>` | Command-validating runtime wrapper |
| `Result<TSuccess, TError>` | Discriminated union with Match, Map, Bind, MapError |
| `AutomatonDiagnostics` | OpenTelemetry-compatible tracing (ActivitySource) |

## License

Apache 2.0
