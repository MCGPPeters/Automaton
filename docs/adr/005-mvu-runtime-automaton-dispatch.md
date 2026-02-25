# ADR-005: MVU Runtime — Automaton Constraint with Dispatch

**Status:** Accepted  
**Date:** 2025-06-01  
**Deciders:** Maurice Peters

## Context

The MVU (Model-View-Update) pattern needs a runtime that:

1. Accepts user events (clicks, keypresses, HTTP responses)
2. Runs the transition function to produce new state + effects
3. Renders the new state into a view
4. Executes effects and feeds results back as new events

We must decide:
- Should MVU use the Automaton constraint (`Dispatch(event)`) or the Decider constraint (`Handle(command)`)?
- Where does input validation live?
- How do errors surface to the user?

## Decision

MVU uses the **Automaton constraint** with `Dispatch(event)`. It does **not** use the Decider.

```csharp
public sealed class MvuRuntime<TAutomaton, TState, TEvent, TEffect, TView>
    where TAutomaton : Automaton<TState, TEvent, TEffect>
```

The MVU runtime wires the shared `AutomatonRuntime` with:
- **Observer** = render the new state into a view
- **Interpreter** = execute effects, return feedback events

```csharp
Observer<TState, TEvent, TEffect> observer = (state, _, _) =>
{
    views.Add(render(state));
    return Task.CompletedTask;
};
```

### Why Not the Decider?

MVU's `Transition` is a **total function** — it always succeeds. Invalid inputs are not rejected; they transition to a state that *represents* the error:

```csharp
// MVU: validation errors become state
public static (Model, Effect) Transition(Model state, Msg msg) =>
    msg switch
    {
        Msg.SubmitForm when string.IsNullOrEmpty(state.Email) =>
            (state with { Errors = ["Email is required"] }, new Effect.None()),
        
        Msg.SubmitForm =>
            (state with { IsSubmitting = true }, new Effect.SubmitToApi(state.Email)),
        
        _ => ...
    };
```

There is no command→event separation because **the UI message IS the event**. There is no persistence concern — the event is consumed and forgotten after the transition.

## Mathematical Grounding

### Transition as Total Function

In MVU, the transition function is **total**: it is defined for every `(state, event)` pair and always produces a result:

$$T : S \times \Sigma \to S \times \Lambda$$

$$\forall\;(s, e) \in S \times \Sigma,\; \exists!\;(s', f) \in S \times \Lambda : T(s, e) = (s', f)$$

This is the defining property of the Mealy machine (ADR-001). There are no partial functions, no exceptions, no rejected inputs. Every message the user can send produces a well-defined state.

### Errors as State, Not Rejection

In the Decider pattern (ADR-004), errors *reject* the input — no events are produced, state is unchanged. In MVU, errors are **part of the state**:

$$\text{Model} = \{\ \text{data},\ \text{errors},\ \text{loading},\ \ldots\ \}$$

The state space $S$ includes error representations. A "validation error" is just another state:

$$T(s, \text{SubmitForm}) = \begin{cases} (s \oplus \{\text{errors} = [\ldots]\}, \text{None}) & \text{if invalid} \\ (s \oplus \{\text{loading} = \text{true}\}, \text{Submit}) & \text{if valid} \end{cases}$$

This means the view function can render errors naturally:

$$\text{view} : S \to V$$

The view is a pure function of state. If the state contains errors, the view shows them. No separate error-handling path is needed.

### View as Pure Function (Functor Morphism)

The render function is a **natural transformation** from states to views:

$$\text{render} : S \to V$$

Since `render` is pure and deterministic, the same state always produces the same view. This is the MVU invariant: **the UI is a pure function of the model**. There is no hidden mutable state in the view layer.

In category theory terms, `render` is a morphism in the category of types. When composed with the transition function, the full MVU loop is:

$$\text{event} \xrightarrow{T} (s', f) \xrightarrow{\text{render}} v$$

### The Elm Architecture Correspondence

Automaton's MVU maps directly to the original Elm Architecture (Czaplicki, 2012):

| Elm | Automaton |
|-----|-----------|
| `Model` | `TState` |
| `Msg` | `TEvent` |
| `Cmd Msg` | `TEffect` |
| `update : Msg → Model → (Model, Cmd Msg)` | `Transition(TState, TEvent) → (TState, TEffect)` |
| `view : Model → Html Msg` | `Render<TState, TView>` delegate |
| Elm Runtime | `MvuRuntime` → `AutomatonRuntime` |

The isomorphism is exact. Elm's `update` IS Automaton's `Transition`. The only difference is that Elm's runtime is built into the compiler, while Automaton's is an explicit class.

### Why Dispatch(event), Not Handle(command)

| Criterion | Dispatch(event) | Handle(command) |
|-----------|----------------|-----------------|
| **Rejection** | Never — all messages produce state | Can reject invalid commands |
| **Error visibility** | Errors are in state → rendered by view | Errors are in Result → must be handled separately |
| **Persistence** | Not needed — events are ephemeral | Events must be valid for persistence |
| **Simplicity** | One function: Transition | Two functions: Decide + Transition |
| **Appropriate when** | Input is always valid (UI events) | Input needs validation before persistence |

MVU messages are **already valid events** — they represent things that happened in the UI (button clicked, text entered, HTTP response received). They don't need validation because they're observations of reality, not commands expressing intent.

### Feedback Loop (Interpreter)

The interpreter enables effects to produce new events, creating a closed loop:

```
User clicks → Dispatch(ButtonClicked)
    → Transition → (LoadingState, FetchData)
        → Interpreter(FetchData) → [DataReceived(data)]
            → Dispatch(DataReceived(data))
                → Transition → (DataLoadedState, None)
                    → Render → UI updated
```

This is the **free monad** pattern: effects are descriptions (data), not executions. The interpreter is the only point of contact with the outside world.

## Consequences

### Positive

- **Simplicity** — one function (`Transition`) handles everything. No command/event split needed for UI.
- **Total function guarantee** — every message produces a well-defined state. No error handling at the dispatch site.
- **View purity** — the UI is always a deterministic function of the model. No hidden state.
- **Testability** — call `Transition` directly, assert on state. No mocking needed.

### Negative

- **No built-in command validation** — if the domain *does* require command validation, developers must implement it inside `Transition` (or use `DecidingRuntime` instead).
- **State bloat** — error information lives in the model, increasing state size. (Acceptable for UI state.)
- **No rejection path** — every message is processed. Malicious or nonsensical messages still produce state transitions. (Acceptable for UI — the messages come from the rendering layer, not external systems.)

### Neutral

- If an MVU application needs domain-level command validation (e.g., a form that talks to an event-sourced backend), the MVU layer dispatches events to update UI state, while the backend uses a Decider for command validation. The two concerns are separate.

## References

- Czaplicki, E. (2012). "Elm: Concurrent FRP for Functional GUIs." Senior thesis, Harvard University.
- Feldman, R. (2019). "Elm in Action." Manning Publications.
- Czaplicki, E. (2016). "The life of a file." Elm Europe talk.
