# Glossary

Key terms used throughout the Automaton documentation, defined in plain English.

---

### Automaton

A deterministic state machine with effects. Given a current state and an event, it produces a new state and an effect. The core interface of this library. See [The Kernel](the-kernel.md).

### Command

A request representing user intent — "I want to add 5 to the counter." Commands are validated by the [Decider](#decider) before producing events. Commands can be rejected; events cannot.

### Decide

A pure function that validates a command against the current state. Returns either a list of events (accepted) or an error (rejected). The core of the [Decider pattern](#decider-pattern).

### Decider

An Automaton that also validates commands. It extends the kernel with `Decide(state, command)` and `IsTerminal(state)`. See [The Decider](the-decider.md).

### Decider Pattern

A pattern for command validation in event-driven systems, formalized by Jérémie Chassaing in 2021. It has seven elements: Command type, Event type, State type, Initial state, Decide, Evolve (Transition), and IsTerminal.

### DecidingRuntime

A runtime wrapper that adds `Handle(command)` on top of `AutomatonRuntime`. It calls `Decide` first, then dispatches the resulting events atomically. See [API Reference: Decider](../reference/decider.md).

### Dispatch

The primary operation on an `AutomatonRuntime` — sends an event through the transition → observe → interpret cycle. One dispatch can trigger multiple transitions if the interpreter returns feedback events.

### Effect

The output of a transition function — a description of a side effect, encoded as data. Effects are *not* executed by the transition function; they are interpreted by the [Interpreter](#interpreter). Examples: `TurnOnHeater`, `SendEmail`, `Log("message")`, `None`.

### Effects as Data

The design principle that side effects are represented as values (data) rather than performed directly. The transition function returns effect *descriptions*; the runtime *executes* them. This keeps the transition function pure and testable. Also known as the "functional core, imperative shell" pattern.

### Event

An input to the automaton's transition function. In Event Sourcing, events are immutable facts that have already happened ("TemperatureRecorded", "OrderPlaced"). In MVU, events are UI messages ("ButtonClicked", "TimerTick").

### Event Sourcing

A persistence pattern where state changes are stored as a sequence of events rather than as mutable state. Current state is always derivable by replaying the event stream through the transition function (a left fold). See [Runtimes Compared](runtimes-compared.md).

### Exhaustive Switch

A C# `switch` expression that handles every possible case. Used in transition functions to ensure every event type is handled. The `_ => throw new UnreachableException()` arm catches any missed cases at compile time (with warnings) and runtime.

### Feedback Event

An event produced by the [Interpreter](#interpreter) and dispatched back into the automaton. Creates a closed loop: effect → interpret → feedback event → transition → effect → ... Bounded by `MaxFeedbackDepth` (64) to prevent infinite loops.

### Interpreter

A function that converts an effect into zero or more feedback events: `Effect → ValueTask<Result<Event[], PipelineError>>`. It's one of the two extension points of the runtime (the other is the [Observer](#observer)). Return `Result.Ok([])` for fire-and-forget effects. Errors propagate as `Result.Err(PipelineError)` values.

### IsTerminal

A function on the Decider that indicates whether the automaton has reached a final state. When `true`, infrastructure can stop processing commands, archive the aggregate, or dispose the actor. Defaults to `false`.

### Kleisli Arrow

A mathematical concept from category theory. In the context of Automaton, `Decide` is a Kleisli arrow: `Command → Reader<State, Result<Events, Error>>`. In plain English: a function that reads an environment (state) and can fail (Result). The Kleisli composition lets you chain such functions while automatically propagating errors.

### Left Fold

A fundamental operation that processes a sequence from left to right, accumulating a result: `fold(seed, f, [a, b, c]) = f(f(f(seed, a), b), c)`. In Automaton, the runtime is a left fold over an event stream: each event transitions the state forward. Event Sourcing rebuilds state by folding stored events through the transition function.

### Mealy Machine

A finite-state transducer invented by George Mealy in 1955. Outputs depend on both current state and input (unlike a Moore machine, where outputs depend only on state). The Automaton kernel is a Mealy machine: `transition : (State × Event) → (State × Effect)`.

### Monadic Left Fold

A left fold where each step can have effects (in the monad `M`): `foldM : (State → Event → M (State, Effect)) → State → [Event] → M State`. In Automaton, `M` is `ValueTask` — each transition step can perform async observation and interpretation.

### MVU (Model-View-Update)

The Elm Architecture. A UI pattern where events update a model (state), and the model is rendered into a view. In Automaton, MVU is the kernel with: Observer = render the view, Interpreter = execute effects and return feedback events. See [Runtimes Compared](runtimes-compared.md).

### Observer

A function that sees each transition triple `(state, event, effect)` after the automaton steps: `(State, Event, Effect) → ValueTask<Result<Unit, PipelineError>>`. It's one of the two extension points of the runtime (the other is the [Interpreter](#interpreter)). Used for rendering (MVU), persisting (ES), or logging. Returns `PipelineResult.Ok` on the happy path (zero-alloc). Errors propagate as `Result.Err(PipelineError)` values.

### Observer Composition

Combining multiple observers into one using monadic combinators. `Then` runs sequentially with short-circuit on error. `Where` guards with a predicate. `Select` contramaps inputs. `Catch` handles errors. `Combine` runs both regardless of individual failures. See [Observer Composition guide](../guides/observer-composition.md).

### PipelineError

`readonly record struct PipelineError(string Message, string? Source, Exception? Exception)` — a structured error from an Observer or Interpreter pipeline stage. Carries a human-readable message, an optional source identifier, and an optional underlying exception. Errors propagate through the dispatch chain via `Result<T, PipelineError>`.

### PipelineResult

A static class with a pre-allocated `Ok` value: `PipelineResult.Ok` returns `ValueTask<Result<Unit, PipelineError>>` wrapping `Result.Ok(Unit.Value)`. Use this in observer implementations instead of constructing a new Result — it's the zero-alloc fast path.

### Projection

A read model built by folding events through a different accumulator than the aggregate state. Projections answer questions the aggregate state can't — e.g., "how many times has the heater cycled on and off?" while the state only knows the current heating status.

### Pure Function

A function with two properties: (1) its return value depends only on its arguments, and (2) it has no side effects. All `Init`, `Transition`, and `Decide` functions in Automaton must be pure. This makes them deterministic, testable, and replayable.

### Reset

An operation on `AutomatonRuntime` that replaces the current state without triggering a transition or observer. Used for hydrating state from external sources (event replay, snapshots).

### Result

`Result<TSuccess, TError>` — a discriminated union that is either `Ok(value)` or `Err(error)`. Used by the Decider to represent command validation outcomes and by Observer/Interpreter pipelines for error propagation. Supports `Map` / `Select` (functor), `Bind` / `SelectMany` (monad), and `MapError`. Implements LINQ query syntax for monadic composition. Implemented as a `readonly struct` for zero heap allocation. See [API Reference: Result](../reference/result.md).

### Runtime

The component that executes the automaton's transition function in a loop. Handles threading, cancellation, effect interpretation, and observation. The `AutomatonRuntime` is the shared base; specialized runtimes (MVU, ES, Actor) wire it with specific observers and interpreters. See [The Runtime](the-runtime.md).

### State

What the automaton remembers between transitions. Represented as an immutable record. The `with` expression creates modified copies. State is the first element of the tuple returned by `Init()` and `Transition()`.

### Tell

The fire-and-forget messaging pattern used by actors. `ActorRef.Tell(message)` enqueues a message in the actor's mailbox — the caller doesn't wait for a response.

### Terminal State

A state from which no further commands should be processed. Indicated by `IsTerminal(state)` returning `true`. Examples: a shipped order, a cancelled subscription, a shut-down device.

### Transition

The pure function at the heart of every automaton: `(State, Event) → (State, Effect)`. Given the current state and an event, it produces the new state and an effect to be executed. Must handle every event type (exhaustive switch).

### Unit

`readonly record struct Unit` — the unit type with exactly one value (`Unit.Value`). Used as the success type in `Result<Unit, PipelineError>` where a success value is required by the type system but no meaningful value exists. Analogous to `void` but expressible as a type parameter.
