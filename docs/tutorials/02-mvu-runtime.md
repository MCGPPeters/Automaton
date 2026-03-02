# Tutorial 02: Building an MVU Runtime

Build a Model-View-Update loop on top of the Automaton kernel.

> **Concept reference:** This tutorial builds a custom runtime. For the underlying theory, see [The Runtime](../concepts/the-runtime.md) and [Building Custom Runtimes](../guides/building-custom-runtimes.md).

## What You'll Learn

- What MVU (Model-View-Update) is and how it maps to the [Automaton kernel](../concepts/the-kernel.md)
- How to write a render function that converts state to views
- How to handle effects and produce feedback events
- How to build a complete MVU runtime from the shared [`AutomatonRuntime`](../reference/runtime.md)

## Prerequisites

Complete [Tutorial 01: Getting Started](01-getting-started.md) first. We'll reuse the same `Counter` automaton.

## MVU in 30 Seconds

The **Elm Architecture** (Model-View-Update) is a UI pattern:

```text
Event → Update(model, event) → (model', cmd) → View(model') → UI
```

If you squint, that's exactly the Automaton kernel:

| Elm | Automaton |
| --- | --------- |
| Model | State |
| Msg | Event |
| Cmd | Effect |
| update | Transition |
| view | Observer (render) |
| cmd handler | Interpreter |

MVU is the Automaton with:
- **Observer** = render the new state into a view
- **Interpreter** = execute effects and return feedback events

## Step 1: Define a Render Function

A render function converts state into a view representation. In a real app this might produce HTML, a virtual DOM, or a terminal UI. For this tutorial, we'll render to strings:

```csharp
public delegate TView Render<in TState, out TView>(TState state);
```

```csharp
Render<CounterState, string> render = state => $"Count: {state.Count}";
```

## Step 2: Build the MVU Runtime

The MVU runtime wraps `AutomatonRuntime` with render-on-transition semantics:

```csharp
using Automaton;

public sealed class MvuRuntime<TAutomaton, TState, TEvent, TEffect, TView>
    where TAutomaton : Automaton<TState, TEvent, TEffect>
{
    private readonly AutomatonRuntime<TAutomaton, TState, TEvent, TEffect> _core;
    private readonly List<TView> _views;

    public TState State => _core.State;
    public IReadOnlyList<TView> Views => _views;
    public IReadOnlyList<TEvent> Events => _core.Events;

    private MvuRuntime(
        AutomatonRuntime<TAutomaton, TState, TEvent, TEffect> core,
        List<TView> views)
    {
        _core = core;
        _views = views;
    }

    public static async Task<MvuRuntime<TAutomaton, TState, TEvent, TEffect, TView>> Start(
        Render<TState, TView> render,
        Interpreter<TEffect, TEvent> interpreter)
    {
        var (state, effect) = TAutomaton.Init();
        var views = new List<TView>();

        // Observer: render the new state after each transition
        Observer<TState, TEvent, TEffect> observer = (s, _, _) =>
        {
            views.Add(render(s));
            return ValueTask.CompletedTask;
        };

        var core = new AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>(
            state, observer, interpreter);

        // Render initial view before effects
        views.Add(render(state));

        // Interpret init effects (may produce feedback → more renders)
        await core.InterpretEffect(effect);

        return new MvuRuntime<TAutomaton, TState, TEvent, TEffect, TView>(core, views);
    }

    public async Task Dispatch(TEvent @event) =>
        await _core.Dispatch(@event);
}
```

Key design decisions:

1. **Render before effects** — The user sees the initial view immediately, before any async init effects complete.
2. **Observer = render** — Each transition renders a new view and appends it to the history.
3. **Delegate to `AutomatonRuntime`** — Thread safety, cancellation, and feedback depth are inherited for free.

## Step 3: Use the Runtime

```csharp
var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
    .Start(
        render: state => $"Count: {state.Count}",
        interpreter: _ => new ValueTask<CounterEvent[]>([]));

// Initial view is rendered immediately
Console.WriteLine(runtime.Views[0]); // "Count: 0"

await runtime.Dispatch(new CounterEvent.Increment());
Console.WriteLine(runtime.Views[1]); // "Count: 1"

await runtime.Dispatch(new CounterEvent.Increment());
await runtime.Dispatch(new CounterEvent.Decrement());
Console.WriteLine(runtime.Views.Last()); // "Count: 1"
```

## Step 4: Handle Effects

So far our counter only produces `CounterEffect.None`. Let's add a `Reset` event that produces a `Log` effect:

```csharp
// Already defined in the counter:
// CounterEvent.Reset → (CounterState(0), CounterEffect.Log("Counter reset from {count}"))
```

Now wire up an interpreter that captures log messages:

```csharp
var logs = new List<string>();

Interpreter<CounterEffect, CounterEvent> interpreter = effect =>
{
    if (effect is CounterEffect.Log log)
    {
        logs.Add(log.Message);
    }
    return new ValueTask<CounterEvent[]>([]);
};

var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
    .Start(state => $"Count: {state.Count}", interpreter);

await runtime.Dispatch(new CounterEvent.Increment());
await runtime.Dispatch(new CounterEvent.Increment());
await runtime.Dispatch(new CounterEvent.Increment());
await runtime.Dispatch(new CounterEvent.Reset());

Console.WriteLine(runtime.State.Count);  // 0
Console.WriteLine(logs[0]);              // "Counter reset from 3"
```

## Step 5: Effects That Produce Feedback Events

The real power of MVU is effects that produce feedback events — an HTTP response, a timer tick, or a WebSocket message:

```csharp
Interpreter<CounterEffect, CounterEvent> interpreter = async effect =>
{
    return effect switch
    {
        // Suppose Log effect triggers an auto-increment after logging
        CounterEffect.Log log =>
        [
            new CounterEvent.Increment()  // feedback event!
        ],

        _ => []
    };
};
```

When the interpreter returns events, they are dispatched back into the automaton loop:

```text
Reset → Transition → (CounterState(0), Log("..."))
                        ↓
                    Observer renders "Count: 0"
                        ↓
                    Interpreter(Log) → [Increment]
                        ↓
                    Increment → Transition → (CounterState(1), None)
                        ↓
                    Observer renders "Count: 1"
```

This is how MVU handles async operations without breaking the unidirectional data flow.

## The Full Picture

```text
┌──────────────────────────────────────────────┐
│                MvuRuntime                     │
│                                              │
│  Event ──► Transition ──► (State', Effect)   │
│                │                │            │
│                ▼                ▼            │
│            Render(State')   Interpreter      │
│              │               │              │
│              ▼               ▼              │
│          View appended   Feedback events     │
│                           │                 │
│                           └──► Dispatch ────┘
└──────────────────────────────────────────────┘
```

## Testing an MVU Runtime

Because the transition function is pure, you can test domain logic without any runtime:

```csharp
// Test the transition directly
var (state, effect) = Counter.Transition(new CounterState(5), new CounterEvent.Increment());
Assert.Equal(6, state.Count);
Assert.IsType<CounterEffect.None>(effect);
```

And test the full MVU loop for integration:

```csharp
var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
    .Start(state => $"Count: {state.Count}", handleEffect);

await runtime.Dispatch(new CounterEvent.Increment());
await runtime.Dispatch(new CounterEvent.Increment());
await runtime.Dispatch(new CounterEvent.Decrement());

Assert.Equal(1, runtime.State.Count);
Assert.Equal(4, runtime.Views.Count); // init + 3 dispatches
Assert.Equal("Count: 0", runtime.Views[0]);
Assert.Equal("Count: 1", runtime.Views[1]);
Assert.Equal("Count: 2", runtime.Views[2]);
Assert.Equal("Count: 1", runtime.Views[3]);
```

## What's Next

- **[Event-Sourced Aggregate](03-event-sourced-aggregate.md)** — The same Counter, now with persistent events
- **[Actor System](04-actor-system.md)** — The same Counter, now with a mailbox
- **[Command Validation](05-command-validation.md)** — Add the Decider pattern to validate commands before producing events

### Deepen Your Understanding

| Topic | Link |
| ----- | ---- |
| How Observer and Interpreter work | [The Runtime](../concepts/the-runtime.md) |
| Chaining multiple observers | [Observer Composition](../guides/observer-composition.md) |
| Building other custom runtimes | [Building Custom Runtimes](../guides/building-custom-runtimes.md) |
| Choosing between MVU, ES, and Actors | [Runtimes Compared](../concepts/runtimes-compared.md) |
