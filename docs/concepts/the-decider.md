# The Decider

The [kernel](the-kernel.md) accepts events and produces state transitions. But in real systems, you need to validate *intent* before producing *facts*. That's what the Decider does.

## The Problem

The basic Automaton accepts any event — there's no validation:

```csharp
// This always succeeds, even if the result doesn't make business sense
await runtime.Dispatch(new CounterEvent.Increment());
```

But real domains have rules:

- Can this counter go above 100?
- Can this order be placed if inventory is empty?
- Can this account be debited if the balance is insufficient?

## The Pattern

The [Decider pattern](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider) (Jérémie Chassaing, 2021) adds a command validation layer:

```text
Command → Decide(state, command) → Result<Events, Error> → Transition(state, event)
```

It separates four concerns:

| Concept | What it represents | Who provides it |
| ------- | ------------------ | --------------- |
| **Command** | User intent ("I want to add 5") | Caller |
| **Decide** | Validates intent against current state | Decider |
| **Event** | Validated fact ("5 was added") | Decide function |
| **Transition** | State evolution | Automaton kernel |

Commands are *requests*. Events are *facts*. The `Decide` function is the gatekeeper.

## The Interface

```csharp
public interface Decider<TState, TCommand, TEvent, TEffect, TError>
    : Automaton<TState, TEvent, TEffect>
{
    static abstract Result<TEvent[], TError> Decide(TState state, TCommand command);
    static virtual bool IsTerminal(TState state) => false;
}
```

Because `Decider<...> : Automaton<...>`, a Decider **is** an Automaton. All existing runtimes continue to work — the Decider is a non-breaking upgrade.

### The Seven Elements

The Decider pattern has seven elements. The Automaton provides four; the Decider adds three:

| # | Element | Source | Method |
| - | ------- | ------ | ------ |
| 1 | Command type | Type parameter | `TCommand` |
| 2 | Event type | Type parameter | `TEvent` |
| 3 | State type | Type parameter | `TState` |
| 4 | Initial state | Automaton | `Init()` |
| 5 | Decide | **Decider** | `Decide(state, command)` |
| 6 | Evolve | Automaton | `Transition(state, event)` |
| 7 | Is terminal | **Decider** | `IsTerminal(state)` |

## Example: Bounded Counter

```csharp
public class Counter
    : Decider<CounterState, CounterCommand, CounterEvent, CounterEffect, CounterError>
{
    public const int MaxCount = 100;

    public static (CounterState, CounterEffect) Init() =>
        (new CounterState(0), new CounterEffect.None());

    public static Result<CounterEvent[], CounterError> Decide(
        CounterState state, CounterCommand command) =>
        command switch
        {
            CounterCommand.Add(var n) when state.Count + n > MaxCount =>
                Result<CounterEvent[], CounterError>
                    .Err(new CounterError.Overflow(state.Count, n, MaxCount)),

            CounterCommand.Add(var n) when state.Count + n < 0 =>
                Result<CounterEvent[], CounterError>
                    .Err(new CounterError.Underflow(state.Count, n)),

            CounterCommand.Add(var n) when n >= 0 =>
                Result<CounterEvent[], CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(
                        new CounterEvent.Increment(), n).ToArray()),

            CounterCommand.Add(var n) =>
                Result<CounterEvent[], CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(
                        new CounterEvent.Decrement(), Math.Abs(n)).ToArray()),

            CounterCommand.Reset when state.Count is 0 =>
                Result<CounterEvent[], CounterError>
                    .Err(new CounterError.AlreadyAtZero()),

            CounterCommand.Reset =>
                Result<CounterEvent[], CounterError>
                    .Ok([new CounterEvent.Reset()]),

            _ => throw new UnreachableException()
        };

    public static (CounterState, CounterEffect) Transition(
        CounterState state, CounterEvent @event) =>
        @event switch
        {
            CounterEvent.Increment =>
                (state with { Count = state.Count + 1 }, new CounterEffect.None()),
            CounterEvent.Decrement =>
                (state with { Count = state.Count - 1 }, new CounterEffect.None()),
            CounterEvent.Reset =>
                (new CounterState(0),
                 new CounterEffect.Log($"Counter reset from {state.Count}")),
            _ => throw new UnreachableException()
        };
}
```

Key points:

- **`Decide` is pure** — same state + same command = same result, always.
- **One command can produce many events** — `Add(5)` produces five `Increment` events.
- **Errors carry context** — `Overflow` tells you *why* it failed, not just *that* it failed.
- **Effects come from `Transition`, not `Decide`** — Decide says "these events happened." Transition says "given this event, here's the new state and what to do."

## The Result Type

`Decide` returns `Result<TEvent[], TError>` — a discriminated union that is either `Ok(events)` or `Err(error)`.

`Result<TSuccess, TError>` is a `readonly struct` — zero heap allocation.

### Construction

```csharp
var ok = Result<int, string>.Ok(42);
var err = Result<int, string>.Err("something went wrong");
```

### Pattern Matching

```csharp
var message = result.Match(
    value => $"Success: {value}",
    error => $"Failed: {error}");
```

### Map (Functor)

Transform the success value, leaving errors untouched:

```csharp
var doubled = Result<int, string>.Ok(21).Map(v => v * 2);
// Ok(42)

var stillErr = Result<int, string>.Err("fail").Map(v => v * 2);
// Err("fail") — unchanged
```

### Bind (Monad)

Chain operations that can themselves fail — railway-oriented programming:

```csharp
var result = parseInput(raw)       // Result<int, Error>
    .Bind(n => validate(n))        // Result<int, Error>
    .Map(n => $"Result: {n}");     // Result<string, Error>
// If any step fails, the error short-circuits through the rest
```

### MapError

Transform the error value:

```csharp
var mapped = Result<int, string>.Err("fail").MapError(e => e.Length);
// Err(4)
```

See the [Error Handling Patterns guide](../guides/error-handling-patterns.md) for advanced recipes.

## DecidingRuntime

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

### Atomicity

The entire `Handle` operation — Decide + all Dispatches — executes under a single lock acquisition. No other `Handle` call can interleave between reading state and dispatching events. This prevents [TOCTOU](https://en.wikipedia.org/wiki/Time-of-check_to_time-of-use) races.

### Terminal State

`IsTerminal` signals that the automaton has reached an end state. When `IsTerminal` returns `true`, infrastructure can stop processing commands, archive the aggregate, or dispose the actor:

```csharp
public static bool IsTerminal(OrderState state) =>
    state.Status is OrderStatus.Shipped or OrderStatus.Cancelled;
```

`IsTerminal` defaults to `false` (never terminal). Override it only when your domain has a natural end-of-life.

## Mathematically

`Decide` is a [Kleisli arrow](https://en.wikipedia.org/wiki/Kleisli_category):

```text
decide : Command → Reader<State, Result<Events, Error>>
```

In plain English: given a command, it reads the current state and returns either events or an error. This is the standard functional pattern for computations that:

1. Read from an environment (the state)
2. Can fail (the Result)

The Kleisli composition lets you chain multiple Decide-like operations while automatically propagating errors — which is exactly what `Result.Bind` does.

## Next

- [**Runtimes Compared**](runtimes-compared.md) — when to use each pattern
- [**Tutorial 05**](../tutorials/05-command-validation.md) — full walkthrough of command validation
- [**API Reference: Decider**](../reference/decider.md) — complete method documentation
