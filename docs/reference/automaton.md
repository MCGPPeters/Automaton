# Automaton&lt;TState, TEvent, TEffect, TParameters&gt;

`namespace Automaton`

The kernel interface — a deterministic state machine with effects (Mealy machine).

## Definition

```csharp
public interface Automaton<TState, TEvent, TEffect, TParameters>
{
    static abstract (TState State, TEffect Effect) Init(TParameters parameters);
    static abstract (TState State, TEffect Effect) Transition(TState state, TEvent @event);
}
```

## Type Parameters

| Parameter | Description |
| --------- | ----------- |
| `TState` | The state of the automaton. Should be an immutable record. |
| `TEvent` | The input events that drive transitions. Typically an interface with nested record structs. |
| `TEffect` | The output effects produced by transitions. Typically an interface with nested record structs. |
| `TParameters` | The parameters required to initialize the automaton. Use `Unit` for parameterless automata. |

## Methods

### Init

```csharp
static abstract (TState State, TEffect Effect) Init(TParameters parameters);
```

Produces the initial state and any startup effect from the given parameters.

Called once when the runtime starts. The init effect is interpreted immediately by the runtime's Interpreter, which may produce feedback events that trigger additional transitions. Use `Unit` as `TParameters` for automata that require no initialization parameters.

**Parameters:**

| Parameter | Description |
| --------- | ----------- |
| `parameters` | The initialization parameters. Use `default` for `Unit`-parameterized automata. |

**Returns:** A tuple of the initial state and a startup effect.

### Transition

```csharp
static abstract (TState State, TEffect Effect) Transition(TState state, TEvent @event);
```

Pure transition function: given the current state and an event, produces the new state and an effect.

**This function must be pure:**

- Its return value depends only on its arguments
- It has no side effects (no I/O, no mutation, no randomness)
- It is total — it handles every possible event (exhaustive `switch`)

**Parameters:**

| Parameter | Description |
| --------- | ----------- |
| `state` | The current state of the automaton. |
| `@event` | The event that triggered the transition. |

**Returns:** A tuple of the new state and an effect to be executed by the runtime.

## Usage

### Implementing an Automaton

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

```csharp
public class Counter : Automaton<CounterState, CounterEvent, CounterEffect, Unit>
{
    public static (CounterState, CounterEffect) Init(Unit _) =>
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

### Testing Directly

Because `Init` and `Transition` are static methods, you can test them without any runtime:

```csharp
var (state, effect) = Counter.Init(default);
Assert.Equal(0, state.Count);

var (next, _) = Counter.Transition(state, new CounterEvent.Increment());
Assert.Equal(1, next.Count);
```

## Remarks

- The Automaton interface uses C# static abstract members, requiring .NET 7+ and a class (not struct) implementation.
- The implementing class itself holds no state — all state flows through the `TState` parameter.
- Effects are data, not actions. The transition function describes what should happen; the [Interpreter](runtime.md#interpreter) makes it happen.

## See Also

- [The Kernel](../concepts/the-kernel.md) — conceptual explanation
- [Decider](decider.md) — extends Automaton with command validation
- [AutomatonRuntime](runtime.md) — executes the transition function in a loop
