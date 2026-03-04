# Composition

Real applications have more than one concern. A thermostat has temperature control. A web app has UI navigation *and* domain logic. An order system has fulfillment *and* billing. Each concern is naturally modeled as its own automaton — but the application needs to run as **one** coherent state machine.

This is the composition problem: how do you combine multiple automata into one?

## The Analogy: `static void Main`

Every application has a composition root — the place where independently defined components are wired together. In a traditional application, that's `static void Main`:

```csharp
public static void Main(string[] args)
{
    var billing = new BillingService(config);
    var fulfillment = new FulfillmentService(config);
    var app = new OrderApp(billing, fulfillment);
    app.Run();
}
```

In the Automaton world, the **composed automaton** plays the same role. Its `Transition` function is the composition root of behavior — the single place where independently defined automata are wired together:

```csharp
public static (AppState, AppEffect) Transition(AppState state, AppEvent @event) =>
    @event switch
    {
        BillingEvent e => ComposeBilling(state, e),
        FulfillmentEvent e => ComposeFulfillment(state, e),
        UiEvent e => ComposeUi(state, e),
        _ => (state, new AppEffect.None())
    };
```

| Concept | Traditional App | Composed Automaton |
| ------- | --------------- | ------------------ |
| **Composition root** | `static void Main` — wires the object graph | `Transition` — wires the behavioral graph |
| **What it owns** | Object lifetimes | Product state |
| **What it delegates to** | Services, repositories | Sub-automata transition functions |
| **When it runs** | Once, at startup | On every event (but the structure is fixed at compile time) |

The key difference: `Main` wires the **object graph** once at startup. `Transition` wires the **behavioral graph** — it's called on every event, but the *structure* of delegation is fixed at compile time. Same role, different dimension: objects vs. state transitions.

## Three Approaches to Multi-Automaton Systems

When you have two automata — say a UI automaton and a domain Decider — there are three ways to combine them:

### ❌ Option 1: Two Runtimes (Sidecar)

Run each automaton in its own `AutomatonRuntime` and bridge them:

```text
┌─────────────────┐     bridge      ┌─────────────────┐
│ UI Runtime      │ ──────────────► │ Domain Runtime   │
│ (AutomatonRT)   │ ◄────────────── │ (DecidingRT)     │
│ state: UIModel  │   events/cmds   │ state: DomainSt  │
└─────────────────┘                 └─────────────────┘
```

**Problems:**
- Two runtimes to manage, coordinate, and lifecycle
- State synchronization between runtimes (operational complexity)
- Event ordering across runtimes is non-trivial
- Two sets of semaphores, two dispatch loops

This is like having two `Main` methods — it works, but the coupling is runtime/operational rather than structural/compile-time.

### ❌ Option 2: Merge Into One

Collapse both automata into a single flat automaton:

```csharp
// Domain Decider absorbs all UI concerns
public class App : Decider<AppState, AppCommand, AppEvent, AppEffect, AppError, Unit>
{
    // Transition handles UI navigation AND domain logic
    // Decide validates UI forms AND business rules
}
```

**Problems:**
- The Decider becomes environment-specific (browser, server, CLI)
- Can't reuse the domain logic in a different runtime (event-sourced backend, SSR)
- Violates hexagonal architecture — the domain knows about the delivery mechanism

### ✅ Option 3: Composed Automaton

One automaton structurally composes both, using product state and delegating to each sub-automaton:

```text
┌──────────────────────────────────────┐
│ Composed Automaton                   │
│ state: (UIModel × DomainState)       │
│                                      │
│ Transition(state, event) =           │
│   match event with                   │
│   | UIEvent → UI transition          │
│   | DomainEvent →                    │
│       DomainDecider.Transition(...)  │
│       + UI state update              │
│                                      │
│ Interpreter(effect) =                │
│   match effect with                  │
│   | Execute(cmd) →                   │
│       DomainDecider.Decide(...)      │
│       → feedback events              │
│   | Navigate → browser push          │
└──────────────────────────────────────┘
         │
         ▼
  ONE AutomatonRuntime
```

**This is the correct approach.** It has:
- **One runtime, one state, one dispatch loop** — no synchronization issues
- **Structural composition** — complexity is at compile time, not runtime
- **Domain remains portable** — the Decider is called as a pure function, not hosted in its own runtime
- **Same per-runtime variation as Option 1** — each delivery mechanism (browser, server, CLI) provides its own composed automaton, but the domain Decider is shared

## Product State

The composed automaton's state is a [product type](https://en.wikipedia.org/wiki/Product_type) — the Cartesian product of each sub-automaton's state:

```csharp
public record AppState(
    UiModel Ui,
    DomainState Domain
);
```

This preserves separation of concerns at the data level. The UI automaton reads and writes `state.Ui`. The domain Decider reads and writes `state.Domain`. The composed `Transition` coordinates both.

## Sum Events

Events from different sub-automata flow through the same dispatch channel. Since the composed automaton is `Automaton<AppState, TEvent, TEffect, TParameters>`, all events must be subtypes of `TEvent`:

```csharp
// Domain events and UI events both implement the composed event type
public sealed record UserAuthenticated(User User) : ConduitEvent, AppEvent;
public sealed record UrlChanged(Url Url) : UiEvent, AppEvent;
```

The composed `Transition` pattern-matches on event type to route to the correct sub-automaton.

## The Delegation Pattern

Inside the composed `Transition`, the domain Decider is called as a **pure function** — not through a runtime, not through a message bus, just a direct function call:

```csharp
public static (AppState, AppEffect) Transition(AppState state, AppEvent @event) =>
    @event switch
    {
        // Domain event: delegate to Decider.Transition, then update UI
        UserAuthenticated(var user) =>
            let (domain', effect) = DomainDecider.Transition(state.Domain, @event)
            in (state with { Domain = domain', Ui = SetUser(state.Ui, user) }, effect),

        // UI-only event: only update UI state
        UrlChanged(var url) =>
            (state with { Ui = Navigate(state.Ui, url) }, new AppEffect.None()),

        _ => (state, new AppEffect.None())
    };
```

The Decider doesn't know it's being composed. It doesn't know about the UI. It's just a pure function being called from within another pure function. This is what makes it portable across runtimes.

## The Interpreter Bridge

Commands from the UI (e.g., "user clicked login") need to reach the domain Decider. The composed automaton's **interpreter** handles this:

```csharp
Interpreter = async (effect) =>
{
    if (effect is Execute(var command))
    {
        var result = DomainDecider.Decide(currentState.Domain, command);
        if (result.IsOk)
            return result.Value;  // events fed back through Transition
        else
            return [new CommandRejected(result.Error)];
    }
    // ... other effects (navigation, HTTP, etc.)
};
```

The interpreter calls `Decide` directly — it's a function call, not a message send. The resulting events feed back through `Transition`, which updates both domain and UI state.

## Multi-Runtime Reuse

The killer advantage of composition over merging: the same Decider works in every runtime.

```text
┌─────────────────────┐   ┌─────────────────────┐   ┌─────────────────────┐
│ Browser (MVU)       │   │ Backend (ES)         │   │ SSR                 │
│                     │   │                      │   │                     │
│ ComposedProgram     │   │ EventSourcedApp      │   │ SsrProgram          │
│ ┌─────────────────┐ │   │ ┌──────────────────┐ │   │ ┌─────────────────┐ │
│ │ ConduitDecider  │ │   │ │ ConduitDecider   │ │   │ │ ConduitDecider  │ │
│ │ (pure function) │ │   │ │ (pure function)  │ │   │ │ (pure function) │ │
│ └─────────────────┘ │   │ └──────────────────┘ │   │ └─────────────────┘ │
│ + UI state          │   │ + event store        │   │ + request context   │
│ + browser interop   │   │ + projections        │   │ + HTML rendering    │
└─────────────────────┘   └─────────────────────┘   └─────────────────────┘
         │                         │                         │
    AutomatonRuntime         DecidingRuntime          AutomatonRuntime
```

The Decider is the **invariant**. The composition and runtime are the **variants**. Each delivery mechanism wraps the same pure domain logic in its own state product and its own interpreter.

## When to Compose

Use composition when:

- You have **multiple concerns** that should evolve independently (UI + domain, billing + fulfillment)
- You need to **reuse domain logic** across different runtimes (browser, server, CLI)
- You want **one runtime instance** with **one consistent state** (no synchronization issues)
- Your sub-automata have **different state shapes** but need to react to **shared events**

Use a single flat automaton when:

- The system has only one concern
- There's no reuse requirement
- The added indirection of composition isn't worth the separation

## Formal Basis

Automaton composition is the **product construction** from automata theory. Given automata $A_1 = (S_1, \Sigma, \delta_1, s_{01}, F_1)$ and $A_2 = (S_2, \Sigma, \delta_2, s_{02}, F_2)$, the product automaton is:

$$A = (S_1 \times S_2, \Sigma, \delta, (s_{01}, s_{02}), F_1 \times F_2)$$

where:

$$\delta((s_1, s_2), a) = (\delta_1(s_1, a), \delta_2(s_2, a))$$

In plain English: both automata see every event and independently transition their own state. The composed state is the pair of individual states. This construction preserves the key properties of each sub-automaton (determinism, totality) while allowing them to operate over a shared event stream.

## Next

- [**The Kernel**](the-kernel.md) — the core interface
- [**The Decider**](the-decider.md) — adding command validation
- [**The Runtime**](the-runtime.md) — how transition functions get executed
- [**Runtimes Compared**](runtimes-compared.md) — when to use each pattern
