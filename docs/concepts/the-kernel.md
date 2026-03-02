# The Kernel

The entire Automaton library is built on one interface and one idea.

## The Interface

```csharp
public interface Automaton<TState, TEvent, TEffect>
{
    static abstract (TState State, TEffect Effect) Init();
    static abstract (TState State, TEffect Effect) Transition(TState state, TEvent @event);
}
```

Two methods. Zero dependencies. That's the whole kernel.

- **`Init()`** — returns the initial state and any startup effect.
- **`Transition(state, event)`** — given the current state and an event, returns the new state and an effect.

## The Idea: Mealy Machines

This interface is a [Mealy machine](https://en.wikipedia.org/wiki/Mealy_machine) — a finite-state transducer invented by George Mealy in 1955. In a Mealy machine, outputs depend on *both* the current state and the input:

```text
transition : (State × Event) → (State × Effect)
```

Compare this to a [Moore machine](https://en.wikipedia.org/wiki/Moore_machine), where outputs depend only on state. The Mealy formulation is more expressive — the same state can produce different effects depending on which event triggered the transition.

### Why not just a state machine?

Traditional finite state machines have states and transitions, but no notion of *output*. They answer "what state am I in?" but not "what should happen next?" The Mealy machine adds effects — descriptions of actions that the runtime should perform.

## Effects as Data

This is the most important design decision in the library.

The transition function **never** performs side effects. It doesn't call APIs, write to databases, send emails, or talk to hardware. Instead, it returns *descriptions* of what should happen:

```csharp
// ❌ Impure — calls hardware directly
public static ThermostatState Transition(ThermostatState state, ThermostatEvent @event)
{
    if (NeedsHeating(state, @event))
        heater.TurnOn(); // side effect inside pure logic!
    return state with { Heating = true };
}

// ✅ Pure — describes the effect as data
public static (ThermostatState, ThermostatEffect) Transition(
    ThermostatState state, ThermostatEvent @event)
{
    if (NeedsHeating(state, @event))
        return (state with { Heating = true }, new ThermostatEffect.TurnOnHeater());
    return (state, new ThermostatEffect.None());
}
```

The effect `TurnOnHeater` is a value — a record struct — not a method call. The [runtime](the-runtime.md) decides what to do with it.

### Why this matters

| Effects as code (impure) | Effects as data (pure) |
| ---- | ---- |
| Can't test without mocking infrastructure | Test the transition function directly |
| Can't replay — side effects already happened | Replay from an event log |
| Can't swap runtimes | Same logic drives MVU, ES, or Actor |
| Time-dependent, order-dependent | Deterministic: same input, same output |
| Hard to reason about | Easy to reason about — it's just a function |

This pattern is known as the [**functional core, imperative shell**](https://www.destroyallsoftware.com/screencasts/catalog/functional-core-imperative-shell) architecture. The domain logic is a pure functional core; the runtime is the imperative shell that executes effects.

## The Three Types

Every automaton is parameterized by three types:

### State — what the machine remembers

Use immutable records. The `with` expression creates modified copies:

```csharp
public record ThermostatState(
    decimal CurrentTemp,
    decimal TargetTemp,
    bool Heating);
```

### Events — what happens (inputs)

Use interfaces with nested record structs for a closed set of cases that work with C# pattern matching:

```csharp
public interface ThermostatEvent
{
    record struct TemperatureReading(decimal Temperature) : ThermostatEvent;
    record struct HeaterStarted : ThermostatEvent;
    record struct HeaterStopped : ThermostatEvent;
}
```

> **Why interfaces with nested record structs?** This gives you a closed set of cases compatible with exhaustive `switch` expressions. Record structs are value types — zero heap allocation per event.

### Effects — what the machine asks the outside world to do (outputs)

Same pattern as events — effects are data, not actions:

```csharp
public interface ThermostatEffect
{
    record struct None : ThermostatEffect;
    record struct TurnOnHeater : ThermostatEffect;
    record struct TurnOffHeater : ThermostatEffect;
}
```

`None` is important — many transitions produce no effect. Having an explicit `None` case keeps the type system honest.

## Pure Transition Functions

The transition function is a total, pure function. Given the same inputs, it always returns the same output:

```csharp
public static (ThermostatState, ThermostatEffect) Transition(
    ThermostatState state, ThermostatEvent @event) =>
    @event switch
    {
        ThermostatEvent.TemperatureReading(var temp) when temp < state.TargetTemp =>
            (state with { CurrentTemp = temp }, new ThermostatEffect.TurnOnHeater()),

        ThermostatEvent.TemperatureReading(var temp) =>
            (state with { CurrentTemp = temp }, new ThermostatEffect.None()),

        ThermostatEvent.HeaterStarted =>
            (state with { Heating = true }, new ThermostatEffect.None()),

        ThermostatEvent.HeaterStopped =>
            (state with { Heating = false }, new ThermostatEffect.None()),

        _ => throw new UnreachableException()
    };
```

Properties of a pure transition function:

- **Deterministic** — same state + same event = same result, always
- **No side effects** — doesn't read clocks, call APIs, or write to disk
- **Total** — handles every possible event (exhaustive `switch`)
- **Testable in isolation** — no mocks, no infrastructure, no async

```csharp
// Test directly — no runtime needed
var (state, effect) = Thermostat.Transition(
    new ThermostatState(18.0m, 22.0m, false),
    new ThermostatEvent.TemperatureReading(17.0m));

Assert.Equal(17.0m, state.CurrentTemp);
Assert.IsType<ThermostatEffect.TurnOnHeater>(effect);
```

## The Historical Lineage

The Automaton kernel is not a new idea. It's a unification of patterns that have been independently discovered across six decades:

```text
Mealy Machine (1955)
  → Actor Model (Hewitt, 1973)
  → Erlang/OTP (Armstrong, 1986)
  → Event Sourcing (Young, 2005)
  → Elm Architecture (Czaplicki, 2012)
  → Automaton (2025)
```

Each of these systems uses the same fundamental structure — a pure function from (state, input) to (state, output) — but names it differently and wraps it in different runtime infrastructure.

## What the Kernel Does Not Do

The kernel is deliberately minimal. It does **not** provide:

- **Persistence** — use an [event store](../tutorials/03-event-sourced-aggregate.md) or the upcoming `Automaton.Patterns` package
- **Threading** — the [runtime](the-runtime.md) handles thread safety
- **Command validation** — the [Decider](the-decider.md) adds this layer
- **Tracing** — the runtime adds [OpenTelemetry spans](../tutorials/06-observability.md)
- **Effect execution** — the [Interpreter](the-runtime.md) handles this

The kernel's only job is to define the shape of a pure transition function. Everything else is composed on top.

## Next

- [**The Runtime**](the-runtime.md) — how the transition function gets executed
- [**The Decider**](the-decider.md) — adding command validation
- [**Quick Start**](../getting-started/index.md) — build one yourself
