# Automaton

**Write once, run everywhere.**

A unified kernel for MVU, Event Sourcing, and the Actor Model — based on the observation that all three are instances of the same mathematical structure: a **Mealy machine** (finite-state transducer with effects).

```text
transition : (State × Event) → (State × Effect)
```

Define your pure domain logic once as a transition function. Then execute it in a browser UI loop, an event-sourced aggregate, or a mailbox actor — without changing a single line.

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

## Three Runtimes

### MVU (Model-View-Update)

Run the automaton as a UI application with a render loop.

```csharp
using Automaton.Mvu;

var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
    .Start(
        render: state => $"Count: {state.Count}",
        effectHandler: _ => Task.FromResult<IEnumerable<CounterEvent>>([]));

await runtime.Dispatch(new CounterEvent.Increment());
// runtime.State.Count == 1
// runtime.Views == ["Count: 0", "Count: 1"]
```

### Event Sourcing

Run the automaton as a persistent aggregate with replay and projections.

```csharp
using Automaton.EventSourcing;

var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

aggregate.Dispatch(new CounterEvent.Increment());
aggregate.Dispatch(new CounterEvent.Increment());
aggregate.Dispatch(new CounterEvent.Decrement());
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

## Namespaces

| Namespace | Contains |
| --------- | -------- |
| `Automaton` | The kernel interface |
| `Automaton.Mvu` | Headless MVU runtime |
| `Automaton.EventSourcing` | Event store, aggregate runner, projections |
| `Automaton.Actor` | Actor reference, mailbox instance |

## License

MIT
