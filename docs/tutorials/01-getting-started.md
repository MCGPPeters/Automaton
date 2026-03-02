# Tutorial 01: Getting Started

Build your first automaton — a smart thermostat with state, events, effects, and a feedback loop.

> **Theory reference:** This tutorial walks through the concepts explained in [The Kernel](../concepts/the-kernel.md) and [The Runtime](../concepts/the-runtime.md). Read those first for the theory, or follow this tutorial for a hands-on introduction.

## What You'll Learn

- How the [Automaton kernel](../concepts/the-kernel.md) works
- How to define state, events, and effects
- How to write a pure transition function
- How to start a [runtime](../concepts/the-runtime.md) and dispatch events
- How the [interpreter](../reference/runtime.md#interpreter) closes the loop by turning effects into feedback events
- How [observer composition](../guides/observer-composition.md) works

## The Kernel

The entire library is built on one interface:

```csharp
public interface Automaton<TState, TEvent, TEffect>
{
    static abstract (TState State, TEffect Effect) Init();
    static abstract (TState State, TEffect Effect) Transition(TState state, TEvent @event);
}
```

That's it. Two methods:

- **`Init()`** — returns the initial state and any startup effect.
- **`Transition(state, event)`** — given the current state and an event, returns the new state and an effect.

This is a [Mealy machine](https://en.wikipedia.org/wiki/Mealy_machine) — a finite-state transducer where outputs depend on both state and input:

```text
transition : (State × Event) → (State × Effect)
```

Effects are data, not actions. The transition function never talks to hardware, databases, or APIs. It describes *what should happen*, and the runtime makes it happen.

## Step 1: Define Your Domain Types

We'll build a smart thermostat that monitors temperature and controls a heater. Every automaton needs three types:

1. **State** — what the machine remembers
2. **Events** — what happens (inputs)
3. **Effects** — what should be done (outputs)

```csharp
using System.Diagnostics;

// State: what the thermostat remembers
public record ThermostatState(
    decimal CurrentTemp,
    decimal TargetTemp,
    bool Heating);

// Events: what can happen
public interface ThermostatEvent
{
    // A sensor reports the current temperature
    record struct TemperatureReading(decimal Temperature) : ThermostatEvent;

    // The user changes the target temperature
    record struct TargetChanged(decimal Target) : ThermostatEvent;

    // Hardware confirms the heater started
    record struct HeaterStarted : ThermostatEvent;

    // Hardware confirms the heater stopped
    record struct HeaterStopped : ThermostatEvent;
}

// Effects: what the thermostat asks the outside world to do
public interface ThermostatEffect
{
    record struct None : ThermostatEffect;
    record struct TurnOnHeater : ThermostatEffect;
    record struct TurnOffHeater : ThermostatEffect;
    record struct SendAlert(string Message) : ThermostatEffect;
}
```

> **Why interfaces with nested record structs?** This gives you a closed set of cases that work with C# pattern matching (exhaustive `switch`). Record structs are value types — zero heap allocation.

Notice the effects: `TurnOnHeater`, `TurnOffHeater`, and `SendAlert` are *descriptions* of side effects, not the side effects themselves. The transition function produces them as data.

## Step 2: Implement the Automaton

```csharp
public class Thermostat
    : Automaton<ThermostatState, ThermostatEvent, ThermostatEffect>
{
    public const decimal AlertThreshold = 35.0m;

    public static (ThermostatState, ThermostatEffect) Init() =>
        (new ThermostatState(
            CurrentTemp: 20.0m,
            TargetTemp: 22.0m,
            Heating: false),
         new ThermostatEffect.None());

    public static (ThermostatState, ThermostatEffect) Transition(
        ThermostatState state, ThermostatEvent @event) =>
        @event switch
        {
            // Sensor reading: update temp, maybe turn heater on or off
            ThermostatEvent.TemperatureReading(var temp) when temp > AlertThreshold =>
                (state with { CurrentTemp = temp },
                 new ThermostatEffect.SendAlert(
                     $"Temperature {temp}°C exceeds alert threshold {AlertThreshold}°C")),

            ThermostatEvent.TemperatureReading(var temp) when temp < state.TargetTemp && !state.Heating =>
                (state with { CurrentTemp = temp },
                 new ThermostatEffect.TurnOnHeater()),

            ThermostatEvent.TemperatureReading(var temp) when temp >= state.TargetTemp && state.Heating =>
                (state with { CurrentTemp = temp },
                 new ThermostatEffect.TurnOffHeater()),

            ThermostatEvent.TemperatureReading(var temp) =>
                (state with { CurrentTemp = temp },
                 new ThermostatEffect.None()),

            // User changes target: re-evaluate whether to heat
            ThermostatEvent.TargetChanged(var target) when state.CurrentTemp < target && !state.Heating =>
                (state with { TargetTemp = target },
                 new ThermostatEffect.TurnOnHeater()),

            ThermostatEvent.TargetChanged(var target) when state.CurrentTemp >= target && state.Heating =>
                (state with { TargetTemp = target },
                 new ThermostatEffect.TurnOffHeater()),

            ThermostatEvent.TargetChanged(var target) =>
                (state with { TargetTemp = target },
                 new ThermostatEffect.None()),

            // Hardware confirmations: update heater status
            ThermostatEvent.HeaterStarted =>
                (state with { Heating = true },
                 new ThermostatEffect.None()),

            ThermostatEvent.HeaterStopped =>
                (state with { Heating = false },
                 new ThermostatEffect.None()),

            _ => throw new UnreachableException()
        };
}
```

Notice:

- **`Transition` is a pure function** — no I/O, no hardware calls, no side effects. Given the same input, it always returns the same output.
- **Effects are decisions, not actions.** The function says "turn on the heater" by returning `TurnOnHeater` — it doesn't actually do it.
- **`HeaterStarted` and `HeaterStopped` are feedback events** — they come from the outside world confirming the effect happened. The transition function handles them like any other event.

## Step 3: Start the Runtime

The `AutomatonRuntime` executes the automaton loop. You give it two callbacks:

| Callback | Signature | Purpose |
| -------- | --------- | ------- |
| **Observer** | `(State, Event, Effect) → ValueTask<Result<Unit, PipelineError>>` | Called after each transition — render, persist, log |
| **Interpreter** | `Effect → ValueTask<Result<Event[], PipelineError>>` | Converts effects to feedback events |

```csharp
using Automaton;

var runtime = await AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect>
    .Start(
        observer: (state, @event, effect) =>
        {
            Console.WriteLine($"[{@event.GetType().Name}] → {state} | Effect: {effect}");
            return PipelineResult.Ok;
        },
        interpreter: effect => new ValueTask<Result<ThermostatEvent[], PipelineError>>(
            Result<ThermostatEvent[], PipelineError>.Ok(effect switch
            {
                // Simulate hardware: turning heater on → confirm it started
                ThermostatEffect.TurnOnHeater =>
                    [new ThermostatEvent.HeaterStarted()],

                // Simulate hardware: turning heater off → confirm it stopped
                ThermostatEffect.TurnOffHeater =>
                    [new ThermostatEvent.HeaterStopped()],

                // Alert: fire-and-forget, no feedback
                ThermostatEffect.SendAlert(var message) => [],

                // No-op
                _ => []
            })));
```

**This is the key insight.** The interpreter closes the loop:

1. A temperature reading arrives → transition says "turn on heater" (`TurnOnHeater`)
2. The interpreter simulates the hardware and returns `[HeaterStarted]`
3. That feedback event is dispatched back into the automaton
4. The transition handles `HeaterStarted` → updates `Heating = true`

All of this happens automatically inside a single `Dispatch` call.

## Step 4: Dispatch Events and Watch the Feedback Loop

```csharp
// Room is cold (18°C), target is 22°C → heater should turn on
await runtime.Dispatch(new ThermostatEvent.TemperatureReading(18.0m));
```

One `Dispatch` call triggers **two transitions**: the temperature reading and the feedback `HeaterStarted`. This is the effect loop in action:

```text
TemperatureReading(18°C)
  → Transition → (state{18°C, heating=false}, TurnOnHeater)
    → Observer logs it
    → Interpreter(TurnOnHeater) → [HeaterStarted]
      → Dispatch(HeaterStarted)
        → Transition → (state{18°C, heating=true}, None)
          → Observer logs it
          → Interpreter(None) → []  ← loop ends
```

```csharp
Console.WriteLine(runtime.State.Heating); // True — heater kicked in automatically!

// Temperature rises to target → heater turns off
await runtime.Dispatch(new ThermostatEvent.TemperatureReading(22.5m));
Console.WriteLine(runtime.State.Heating); // False

// Dangerously hot → alert (fire-and-forget, no feedback)
await runtime.Dispatch(new ThermostatEvent.TemperatureReading(36.0m));

// User changes target → re-evaluates heater
await runtime.Dispatch(new ThermostatEvent.TargetChanged(38.0m));
Console.WriteLine(runtime.State.Heating); // True — heater restarted!
```

## Step 5: Access State and History

```csharp
// Current state
Console.WriteLine(runtime.State);
// ThermostatState { CurrentTemp = 36.0, TargetTemp = 38.0, Heating = True }

// All events — including feedback events generated by the interpreter
Console.WriteLine($"Total events: {runtime.Events.Count}"); // 8
foreach (var e in runtime.Events)
    Console.WriteLine($"  {e.GetType().Name}");
// TemperatureReading
// HeaterStarted        ← feedback
// TemperatureReading
// HeaterStopped        ← feedback
// TemperatureReading
// TargetChanged
// HeaterStarted        ← feedback
```

## Production Guarantees

Even in this simple example, the runtime gives you:

| Guarantee | How |
| --------- | --- |
| **Thread safety** | All dispatches are serialized via `SemaphoreSlim` — concurrent sensor readings are queued, never interleaved |
| **Cancellation** | Pass a `CancellationToken` to `Start` and `Dispatch` |
| **Feedback depth** | Interpreter loops bounded at 64 depth — a misconfigured interpreter can't stack overflow |
| **Null safety** | Observer and interpreter validated at construction |

```csharp
// With cancellation
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await runtime.Dispatch(new ThermostatEvent.TemperatureReading(19.0m), cts.Token);
```

## Observer Composition

You can chain multiple observers with `Then`:

```csharp
Observer<ThermostatState, ThermostatEvent, ThermostatEffect> logger =
    (state, @event, effect) =>
    {
        Console.WriteLine($"[LOG] {@event.GetType().Name} → {state}");
        return PipelineResult.Ok;
    };

Observer<ThermostatState, ThermostatEvent, ThermostatEffect> alertCapture =
    (state, @event, effect) =>
    {
        if (effect is ThermostatEffect.SendAlert(var message))
            Console.WriteLine($"🚨 ALERT: {message}");
        return PipelineResult.Ok;
    };

Observer<ThermostatState, ThermostatEvent, ThermostatEffect> metrics =
    (state, @event, effect) =>
    {
        // Record metrics: track heater on/off cycles, alert frequency, etc.
        return PipelineResult.Ok;
    };

var combined = logger.Then(alertCapture).Then(metrics);

var runtime = await AutomatonRuntime<Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect>
    .Start(combined, interpreter);
```

All three observers run sequentially for each transition. If any returns `Err`, subsequent observers are skipped (short-circuit via `Then`). See [Observer Composition](../guides/observer-composition.md) for `Where`, `Catch`, `Combine`, and other combinators.

## Why Effects as Data?

You might wonder: why not just call the hardware directly inside `Transition`?

| Effects as code (impure) | Effects as data (pure) |
| -------------------------| ---------------------- |
| Transition calls `heater.TurnOn()` | Transition returns `TurnOnHeater` |
| Can't test without mocking hardware | Test the transition function directly |
| Can't replay — side effects already happened | Can replay from event log |
| Can't swap runtimes | Same logic drives MVU, ES, or Actor |
| Time-dependent, order-dependent | Deterministic — same input, same output |

```csharp
// Pure: test without any infrastructure
var (state, effect) = Thermostat.Transition(
    new ThermostatState(18.0m, 22.0m, false),
    new ThermostatEvent.TemperatureReading(17.0m));

Assert.Equal(17.0m, state.CurrentTemp);                 // Temperature updated
Assert.False(state.Heating);                            // State hasn't changed yet
Assert.IsType<ThermostatEffect.TurnOnHeater>(effect);   // Effect says "turn on heater"
// The interpreter will handle the actual hardware call
```

## What's Next

You now have a running automaton with real effects and a feedback loop. The same `Thermostat` definition — the same `Init` and `Transition` — can drive completely different runtimes:

- **[MVU Runtime](02-mvu-runtime.md)** — Add a view function and render a thermostat UI
- **[Event-Sourced Aggregate](03-event-sourced-aggregate.md)** — Persist temperature events and rebuild state
- **[Actor System](04-actor-system.md)** — Process sensor readings from a mailbox

That's the power of the kernel: write your domain logic once, run it everywhere. See [Runtimes Compared](../concepts/runtimes-compared.md) for help choosing.

### Deepen Your Understanding

| Topic | Link |
| ----- | ---- |
| How the kernel works formally | [The Kernel](../concepts/the-kernel.md) |
| Observer + Interpreter architecture | [The Runtime](../concepts/the-runtime.md) |
| Testing pure functions and runtimes | [Testing Strategies](../guides/testing-strategies.md) |
| Full API signatures | [Automaton Reference](../reference/automaton.md), [Runtime Reference](../reference/runtime.md) |
