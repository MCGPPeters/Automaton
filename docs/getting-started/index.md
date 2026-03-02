# Quick Start

Build a working automaton in 5 minutes.

## Install

```bash
dotnet add package Automaton
```

> **Requires** .NET 10.0 SDK or later.

## Define Your Domain

An automaton needs three types: **state** (what it remembers), **events** (what happens), and **effects** (what it asks the outside world to do).

```csharp
using System.Diagnostics;

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
```

## Implement the Automaton

Two methods — `Init` and `Transition` — define the entire behavior:

```csharp
public class Counter : Automaton<CounterState, CounterEvent, CounterEffect>
{
    public static (CounterState, CounterEffect) Init() =>
        (new CounterState(0), new CounterEffect.None());

    public static (CounterState, CounterEffect) Transition(
        CounterState state, CounterEvent @event) =>
        @event switch
        {
            CounterEvent.Increment =>
                (state with { Count = state.Count + 1 }, new CounterEffect.None()),
            CounterEvent.Decrement =>
                (state with { Count = state.Count - 1 }, new CounterEffect.None()),
            _ => throw new UnreachableException()
        };
}
```

`Transition` is a **pure function** — no I/O, no side effects. Given the same inputs, it always returns the same output.

## Run It

```csharp
using Automaton;

var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>
    .Start(
        observer: (state, @event, effect) =>
        {
            Console.WriteLine($"{@event.GetType().Name} → Count: {state.Count}");
            return PipelineResult.Ok;
        },
        interpreter: _ => new ValueTask<Result<CounterEvent[], PipelineError>>(
            Result<CounterEvent[], PipelineError>.Ok([])));

await runtime.Dispatch(new CounterEvent.Increment());
// Prints: Increment → Count: 1

await runtime.Dispatch(new CounterEvent.Increment());
// Prints: Increment → Count: 2

await runtime.Dispatch(new CounterEvent.Decrement());
// Prints: Decrement → Count: 1

Console.WriteLine(runtime.State.Count); // 1
```

## What Just Happened?

You defined a pure state machine and plugged it into a runtime that handles:

- **Thread safety** — concurrent dispatches are serialized automatically
- **Effect interpretation** — effects can produce feedback events (empty here)
- **Observation** — you see every transition triple `(state, event, effect)`
- **Cancellation** — pass `CancellationToken` to any async method

The same `Counter` definition — unchanged — can drive an [MVU runtime](../tutorials/02-mvu-runtime.md), an [event-sourced aggregate](../tutorials/03-event-sourced-aggregate.md), or a [mailbox actor](../tutorials/04-actor-system.md).

## Next Steps

- [**Installation**](installation.md) — Detailed setup, project templates, prerequisites
- [**The Kernel**](../concepts/the-kernel.md) — Understand the Mealy machine abstraction
- [**Tutorial 01**](../tutorials/01-getting-started.md) — Build a thermostat with a feedback loop
