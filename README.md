# Automaton

**Write your domain logic once. Run it everywhere.**

A minimal, production-hardened Mealy machine kernel for building state machines, MVU runtimes, event-sourced aggregates, and actor systems in .NET — based on the observation that all three are instances of the same mathematical structure: a **Mealy machine** (finite-state transducer with effects).

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
// Pure domain logic — no framework imports, no infrastructure
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

This single definition can drive an MVU runtime, an event-sourced aggregate, or a mailbox actor.

## The Shared Runtime

The `AutomatonRuntime` executes the loop: **dispatch → transition → observe → interpret**, parameterized by two extension points:

| Extension Point | Signature | Purpose |
| --------------- | --------- | ------- |
| **Observer** | `(State, Event, Effect) → ValueTask<Result<Unit, PipelineError>>` | See each transition triple (render, persist, log) |
| **Interpreter** | `Effect → ValueTask<Result<Event[], PipelineError>>` | Convert effects to feedback events |

```csharp
// Observer: sees each (state, event, effect) triple after transition
public delegate ValueTask<Result<Unit, PipelineError>> Observer<in TState, in TEvent, in TEffect>(
    TState state, TEvent @event, TEffect effect);

// Interpreter: converts effects to feedback events
public delegate ValueTask<Result<TEvent[], PipelineError>> Interpreter<in TEffect, TEvent>(TEffect effect);
```

Errors propagate as `Result` values through the pipeline — not as exceptions.

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
| **Error propagation** | Observer and Interpreter return `Result<T, PipelineError>` — errors are values, not exceptions. |

### Observer Composition

Observers compose with monadic combinators:

```csharp
// Sequential (short-circuits on error)
var pipeline = persistObserver.Then(logObserver).Then(metricsObserver);

// Conditional
var heaterOnly = logObserver.Where((_, e, _) => e is HeaterTurnedOn or HeaterTurnedOff);

// Error recovery
var resilient = persistObserver.Catch(err => Result<Unit, PipelineError>.Ok(Unit.Value));

// Both run regardless of individual failures
var both = persistObserver.Combine(notifier);
```

### Building Custom Runtimes

The `AutomatonRuntime` is the building block for specialized runtimes. Each runtime is just specific Observer and Interpreter wiring:

| Runtime Pattern | Observer | Interpreter |
| --------------- | -------- | ----------- |
| **MVU** | Render the new state | Execute effects, return feedback events |
| **Event Sourcing** | Append event to store | No-op (empty) |
| **Actor** | No-op (state is internal) | Execute effect with self-reference |

For combining multiple automata into a single runtime, see [Composition](docs/concepts/composition.md).

## The Decider — Command Validation

The **Decider pattern** ([Chassaing, 2021](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)) adds a command validation layer to the Automaton. It separates *intent* (commands) from *facts* (events):

```text
Command → Decide(state, command) → Result<Events, Error> → Transition(state, event) → (State', Effect)
```

A Decider is an Automaton that also validates commands:

```csharp
public interface Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
    : Automaton<TState, TEvent, TEffect, TParameters>
{
    static abstract Result<TEvent[], TError> Decide(TState state, TCommand command);
    static virtual bool IsTerminal(TState state) => false;
}
```

Together with the Automaton's `Initialize` and `Transition`, this gives the seven elements of the Decider pattern:

| Element | Provided by | Method |
| ------- | ----------- | ------ |
| Command type | Type parameter | `TCommand` |
| Event type | Type parameter | `TEvent` |
| State type | Type parameter | `TState` |
| Initial state | Automaton | `Initialize(parameters)` |
| Decide | Decider | `Decide(state, command)` |
| Evolve | Automaton | `Transition(state, event)` |
| Is terminal | Decider | `IsTerminal(state)` |

### Example: Bounded Counter

```csharp
public class Counter
    : Decider<CounterState, CounterCommand, CounterEvent, CounterEffect, CounterError, Unit>
{
    public const int MaxCount = 100;

    public static (CounterState, CounterEffect) Initialize(Unit _) =>
        (new CounterState(0), new CounterEffect.None());

    public static Result<CounterEvent[], CounterError> Decide(
        CounterState state, CounterCommand command) =>
        command switch
        {
            CounterCommand.Add(var n) when state.Count + n > MaxCount =>
                Result<CounterEvent[], CounterError>
                    .Err(new CounterError.Overflow(state.Count, n, MaxCount)),

            CounterCommand.Add(var n) when n >= 0 =>
                Result<CounterEvent[], CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(
                        new CounterEvent.Increment(), n).ToArray()),

            // ... Transition remains unchanged
        };

    public static (CounterState, CounterEffect) Transition(
        CounterState state, CounterEvent @event) =>
        @event switch
        {
            CounterEvent.Increment => (state with { Count = state.Count + 1 }, new CounterEffect.None()),
            CounterEvent.Decrement => (state with { Count = state.Count - 1 }, new CounterEffect.None()),
            _ => throw new UnreachableException()
        };
}
```

### DecidingRuntime

The `DecidingRuntime` wraps `AutomatonRuntime` and adds `Handle(command)`:

```csharp
var runtime = await DecidingRuntime<Counter, CounterState, CounterCommand,
    CounterEvent, CounterEffect, CounterError, Unit>.Start(default, observer, interpreter);

// Valid command → events dispatched, state updated
var result = await runtime.Handle(new CounterCommand.Add(5));
// result is Ok(CounterState { Count = 5 })

// Invalid command → error returned, state unchanged
var overflow = await runtime.Handle(new CounterCommand.Add(200));
// overflow is Err(CounterError.Overflow { Current = 5, Amount = 200, Max = 100 })
// runtime.State.Count is still 5
```

The entire `Handle` operation — Decide + all Dispatches — executes under a single lock acquisition, preventing [TOCTOU](https://en.wikipedia.org/wiki/Time-of-check_to_time-of-use) races.

Since `Decider<...> : Automaton<...>`, upgrading is **non-breaking** — all existing runtimes continue to work.

### Result Type

`Result<TSuccess, TError>` is a `readonly struct` discriminated union — either `Ok(value)` or `Err(error)`. Zero heap allocation per result.

```csharp
var result = Counter.Decide(state, command);

// Pattern matching
var message = result.IsOk
    ? $"Produced {result.Value.Length} events"
    : $"Rejected: {result.Error}";

// Exhaustive fold (catamorphism)
var text = result.Match(
    ok: events => $"Produced {events.Length} events",
    err: error => $"Rejected: {error}");

// LINQ query syntax (railway-oriented programming)
var final =
    from events in Counter.Decide(state, addCmd)
    from count in Result<int, CounterError>.Ok(events.Length)
    select $"{count} events produced";

// Fluent API — Functor (Map), Monad (Bind), Bifunctor (MapError)
result.Map(events => events.Length)
      .Bind(count => count > 0
          ? Result<string, CounterError>.Ok($"{count} events")
          : Result<string, CounterError>.Err(new CounterError.AlreadyAtZero()));
```

## Observability — OpenTelemetry Tracing

The runtime emits distributed tracing spans via `System.Diagnostics.ActivitySource` — zero external dependencies, compatible with any OpenTelemetry collector.

### Enabling Tracing

Register the source name with your telemetry pipeline:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(AutomatonDiagnostics.SourceName));
```

When no listener is registered, instrumentation has near-zero overhead (`StartActivity()` returns `null`).

### Span Coverage

| Span Name | Tags |
| --------- | ---- |
| `Automaton.Start` | `automaton.type`, `automaton.state.type` |
| `Automaton.Dispatch` | `automaton.type`, `automaton.event.type` |
| `Automaton.InterpretEffect` | `automaton.type`, `automaton.effect.type` |
| `Automaton.Decider.Start` | `automaton.type`, `automaton.state.type` |
| `Automaton.Decider.Handle` | `automaton.type`, `automaton.command.type`, `automaton.result`, `automaton.error.type` |

Command rejections set `automaton.result = "error"` but use `ActivityStatusCode.Ok` — a rejected command is a correct business outcome, not a fault.

## The Proof: It's All the Same Fold

```csharp
var events = new CounterEvent[] { new Increment(), new Increment(), new Decrement() };
var (seed, _) = Counter.Initialize(default);

var finalState = events.Aggregate(seed, (state, @event) =>
    Counter.Transition(state, @event).State);

// finalState.Count == 1
```

MVU, Event Sourcing, and the Actor Model are all left folds over an event stream. The runtime is the variable. The transition function is the invariant.

## Why This Matters

| Traditional approach | Automaton approach |
| -------------------- | ------------------ |
| Domain logic coupled to UI framework | Domain logic is pure — zero dependencies |
| Rewrite business rules for each tier | Write once, run in browser + server + actor |
| Test through infrastructure | Test the transition function directly |
| Framework dictates architecture | Math dictates architecture, framework is pluggable |
| Validation scattered across layers | Validation is a pure function on the Decider |

## Architecture

```text
┌─────────────────────────────────────────────────────┐
│           Automaton<S, E, F, P>                      │
│  Initialize(parameters) + Transition(state, event)   │
└──────────────────────────┬──────────────────────────┘
                           │
            ┌──────────────┴──────────────┐
            │  Decider<S, C, E, F, Err, P> │
            │  Decide(state, command)       │
            │  IsTerminal(state)            │
            └──────────────┬──────────────┘
                           │
           ┌───────────────┴───────────────┐
           │  AutomatonRuntime<A,S,E,F,P>   │
           │  Observer + Interpreter        │
           │  AutomatonDiagnostics          │
           └─────┬──────────────────┬──────┘
                 │                  │
           ┌─────┴──────┐  ┌───────┴────────┐
           │  Your MVU   │  │  Your ES /     │
           │  Runtime    │  │  Actor / ...   │
           └─────────────┘  └────────────────┘
```

Multiple automata can be [composed](docs/concepts/composition.md) into a single automaton via product state and sum events — the automata-theoretic product construction.

## What's in the Box

| Type | Purpose |
| ---- | ------- |
| `Automaton<TState, TEvent, TEffect, TParameters>` | Mealy machine interface (Initialize + Transition) |
| `AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>` | Thread-safe async runtime (dispatch → transition → observe → interpret) |
| `Observer<TState, TEvent, TEffect>` | Transition observer delegate (`→ Result<Unit, PipelineError>`) |
| `Interpreter<TEffect, TEvent>` | Effect interpreter delegate (`→ Result<Event[], PipelineError>`) |
| `ObserverExtensions` | Monadic combinators: `Then`, `Where`, `Select`, `Catch`, `Combine` |
| `InterpreterExtensions` | Monadic combinators: `Then`, `Where`, `Select`, `Catch` |
| `Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>` | Command validation interface (Decide + IsTerminal) |
| `DecidingRuntime<...>` | Command-validating runtime wrapper with atomic Handle |
| `Result<TSuccess, TError>` | `readonly struct` discriminated union with Match, Map, Bind, MapError, LINQ syntax |
| `PipelineError` | Structured error for Observer/Interpreter pipelines |
| `PipelineResult` | Pre-allocated `Ok` value for zero-alloc observer fast path |
| `AutomatonDiagnostics` | OpenTelemetry-compatible tracing (ActivitySource) |

## Packages

| Package | Description | Status |
| ------- | ----------- | ------ |
| [`Automaton`](https://www.nuget.org/packages/Automaton) | Core kernel, runtime, Decider, Result, diagnostics | ✅ Stable |
| `Automaton.Patterns` | Production Event Sourcing, Saga, ConflictResolver | ✅ Stable |
| `Automaton.Resilience` | Retry, Timeout, Circuit Breaker, Fallback, Rate Limiter, Hedging — all as Mealy machines | ✅ Stable |

## Benchmarks

Continuous benchmarks run on every push to `main` via [BenchmarkDotNet](https://benchmarkdotnet.org/). Performance regressions exceeding 150% automatically fail the build.

📊 **[Live dashboard →](https://MCGPPeters.github.io/Automaton/dev/bench/)**

## Documentation

Full documentation with concepts, tutorials, how-to guides, and API reference:

- [**Concepts**](docs/concepts/) — The Kernel, The Runtime, The Decider, Composition, Glossary
- [**Tutorials**](docs/tutorials/) — step-by-step guides for building systems with the kernel
- [**How-To Guides**](docs/guides/) — Observer composition, testing, error handling, custom runtimes
- [**API Reference**](docs/reference/) — complete type and method documentation
- [**Architecture Decision Records**](docs/adr/) — design rationale with mathematical grounding

## License

Apache 2.0
