# Automaton

**Write once, run everywhere.**

A unified kernel for MVU, Event Sourcing, and the Actor Model — based on the observation that all three are instances of the same mathematical structure: a **Mealy machine** (finite-state transducer with effects).

```text
transition : (State × Event) → (State × Effect)
```

Define your pure domain logic once as a transition function. Then execute it in a browser UI loop, an event-sourced aggregate, or a mailbox actor — without changing a single line.

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

This single definition drives all three runtimes below.

## The Shared Runtime

All three runtimes are structurally identical: a **monadic left fold** over an event stream, parameterized by two extension points:

| Extension Point | Signature | Purpose |
| --------------- | --------- | ------- |
| **Observer** | `(State, Event, Effect) → Task` | See each transition triple (render, persist, log) |
| **Interpreter** | `Effect → Task<IEnumerable<Event>>` | Convert effects to feedback events |

```csharp
// Observer: sees each (state, event, effect) triple after transition
public delegate Task Observer<in TState, in TEvent, in TEffect>(
    TState state, TEvent @event, TEffect effect);

// Interpreter: converts effects to feedback events
public delegate Task<IEnumerable<TEvent>> Interpreter<in TEffect, TEvent>(TEffect effect);
```

The `AutomatonRuntime` executes the loop: **dispatch → transition → observe → interpret**.

```csharp
var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>
    .Start(
        observer: (state, @event, effect) =>
        {
            Console.WriteLine($"{@event} → {state}");
            return Task.CompletedTask;
        },
        interpreter: _ => Task.FromResult<IEnumerable<CounterEvent>>([]));

await runtime.Dispatch(new CounterEvent.Increment());
// Prints: Increment → CounterState { Count = 1 }
```

### Observer Composition

Observers compose sequentially with `Then`:

```csharp
var combined = renderObserver.Then(logObserver).Then(metricsObserver);
```

### How Each Runtime Wires the Shared Core

| Runtime | Observer | Interpreter |
| ------- | -------- | ----------- |
| **MVU** | Render the new state | Execute effects, return feedback events |
| **Event Sourcing** | Append event to store + record effect | No-op (empty) |
| **Actor** | No-op (state is internal) | Execute effect with self-reference |

## Three Runtimes

### MVU (Model-View-Update)

Run the automaton as a UI application with a render loop.

```csharp
using Automaton.Mvu;

var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
    .Start(
        render: state => $"Count: {state.Count}",
        interpreter: _ => Task.FromResult<IEnumerable<CounterEvent>>([]));;

await runtime.Dispatch(new CounterEvent.Increment());
// runtime.State.Count == 1
// runtime.Views == ["Count: 0", "Count: 1"]
```

### Event Sourcing

Run the automaton as a persistent aggregate with replay and projections.

```csharp
using Automaton.EventSourcing;

var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

await aggregate.Dispatch(new CounterEvent.Increment());
await aggregate.Dispatch(new CounterEvent.Increment());
await aggregate.Dispatch(new CounterEvent.Decrement());
// aggregate.State.Count == 1
// aggregate.Store.Events.Count == 3

// Rebuild state from scratch (simulates loading from disk)
var rebuilt = aggregate.Rebuild();
// rebuilt.Count == 1

// Build a read model from the event stream
var totalIncrements = new Projection<CounterEvent, int>(
    initial: 0,
    apply: (count, e) => e is CounterEvent.Increment ? count + 1 : count);

totalIncrements.Project(aggregate.Store);
// totalIncrements.ReadModel == 2 (state is 1, but 2 increments occurred)
```

### Actor Model

Run the automaton as a mailbox-based actor with async message processing.

```csharp
using Automaton.Actor;

var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>
    .Spawn("counter-1");

await actor.Ref.Tell(new CounterEvent.Increment());
await actor.Ref.Tell(new CounterEvent.Increment());
await actor.DrainMailbox();
// actor.State.Count == 2
```

## The Decider — Command Validation

The **Decider pattern** ([Chassaing, 2021](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)) adds a command validation layer to the Automaton. It separates *intent* (commands) from *facts* (events):

```text
Command → Decide(state) → Result<Events, Error> → Transition(state, event) → (State', Effect)
```

A Decider is an Automaton that also validates commands:

```csharp
public interface Decider<TState, TCommand, TEvent, TEffect, TError>
    : Automaton<TState, TEvent, TEffect>
{
    static abstract Result<IEnumerable<TEvent>, TError> Decide(TState state, TCommand command);
    static virtual bool IsTerminal(TState state) => false;
}
```

Together with the Automaton's `Init` and `Transition`, this gives the seven elements of the Decider pattern:

| Element | Provided by | Method |
| ------- | ----------- | ------ |
| Command type | Type parameter | `TCommand` |
| Event type | Type parameter | `TEvent` |
| State type | Type parameter | `TState` |
| Initial state | Automaton | `Init()` |
| Decide | Decider | `Decide(state, command)` |
| Evolve | Automaton | `Transition(state, event)` |
| Is terminal | Decider | `IsTerminal(state)` |

### Example: Bounded Counter

The same Counter gains command validation by implementing `Decider` instead of `Automaton`:

```csharp
public interface CounterCommand
{
    record struct Add(int Amount) : CounterCommand;
    record struct Reset : CounterCommand;
}

public interface CounterError
{
    record struct Overflow(int Current, int Amount, int Max) : CounterError;
    record struct Underflow(int Current, int Amount) : CounterError;
    record struct AlreadyAtZero : CounterError;
}

public class Counter
    : Decider<CounterState, CounterCommand, CounterEvent, CounterEffect, CounterError>
{
    public const int MaxCount = 100;

    // Init and Transition remain unchanged — same pure functions as before

    public static Result<IEnumerable<CounterEvent>, CounterError> Decide(
        CounterState state, CounterCommand command) =>
        command switch
        {
            CounterCommand.Add(var n) when state.Count + n > MaxCount =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Err(new CounterError.Overflow(state.Count, n, MaxCount)),

            CounterCommand.Add(var n) when state.Count + n < 0 =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Err(new CounterError.Underflow(state.Count, n)),

            CounterCommand.Add(var n) when n >= 0 =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(new CounterEvent.Increment(), n)),

            CounterCommand.Add(var n) =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(new CounterEvent.Decrement(), Math.Abs(n))),

            CounterCommand.Reset when state.Count is 0 =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Err(new CounterError.AlreadyAtZero()),

            CounterCommand.Reset =>
                new Result<IEnumerable<CounterEvent>, CounterError>
                    .Ok([new CounterEvent.Reset()]),

            _ => throw new UnreachableException()
        };
}
```

### DecidingRuntime

The `DecidingRuntime` wraps `AutomatonRuntime` and adds `Handle(command)`:

```csharp
var runtime = await DecidingRuntime<Counter, CounterState, CounterCommand,
    CounterEvent, CounterEffect, CounterError>.Start(observer, interpreter);

// Valid command → events dispatched, state updated
var result = await runtime.Handle(new CounterCommand.Add(5));
// result is Ok(CounterState { Count = 5 })

// Invalid command → error returned, state unchanged
var overflow = await runtime.Handle(new CounterCommand.Add(200));
// overflow is Err(CounterError.Overflow { Current = 5, Amount = 200, Max = 100 })
// runtime.State.Count is still 5
```

### Result Type

`Result<TSuccess, TError>` is the standard functional error handling type — a sum type that is either `Ok(value)` or `Err(error)`:

```csharp
var result = Counter.Decide(state, command);

// Exhaustive pattern match
var message = result.Match(
    events => $"Produced {events.Count()} events",
    error => $"Rejected: {error}");

// Functor (Map), Monad (Bind), Bifunctor (MapError)
result.Map(events => events.Count())
      .Bind(count => count > 0
          ? new Result<string, CounterError>.Ok($"{count} events")
          : new Result<string, CounterError>.Err(new CounterError.AlreadyAtZero()));
```

### Backward Compatibility

Since `Decider<...> : Automaton<...>`, upgrading from Automaton to Decider is **non-breaking**. All existing runtime usages (MVU, ES, Actor) continue to work unchanged — the Counter still satisfies `Automaton<CounterState, CounterEvent, CounterEffect>`.

## The Proof: It's All the Same Fold

```csharp
var events = new CounterEvent[] { new Increment(), new Increment(), new Decrement() };
var (seed, _) = Counter.Init();

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
┌───────────────────────────────────────────────┐
│             Automaton<S, E, F>                 │
│     Init() + Transition(state, event)          │
└───────────────────────┬───────────────────────┘
                        │
         ┌──────────────┴──────────────┐
         │  Decider<S, C, E, F, Err>   │
         │  Decide(state, command)      │
         │  IsTerminal(state)           │
         └──────────────┬──────────────┘
                        │
          ┌─────────────┴─────────────┐
          │  AutomatonRuntime<A,S,E,F> │
          │  Observer + Interpreter    │
          └─────┬────┬────┬────┬──────┘
                │    │    │    │
          ┌─────┴─┐ ┌┴────┐ ┌─┴────┐ ┌──────────┐
          │ MVU   │ │ ES  │ │Actor │ │ Deciding │
          │Runtime│ │Rntm │ │Rntm  │ │ Runtime  │
          └───────┘ └─────┘ └──────┘ └──────────┘
```

## Namespaces

| Namespace | Contains |
| --------- | -------- |
| `Automaton` | The kernel interface, Decider interface, Result type, shared runtime, Observer, Interpreter |
| `Automaton.Mvu` | Headless MVU runtime |
| `Automaton.EventSourcing` | Event store, aggregate runner, projections |
| `Automaton.Actor` | Actor reference, mailbox instance |

## License

Apache 2.0
