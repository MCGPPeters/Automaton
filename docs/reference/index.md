# API Reference

Complete type and method documentation for the `Automaton` package.

## Types

| Type | Namespace | Purpose |
| ---- | --------- | ------- |
| [`Automaton<TState, TEvent, TEffect>`](automaton.md) | `Automaton` | The kernel interface — Init + Transition |
| [`AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>`](runtime.md) | `Automaton` | Thread-safe async runtime |
| [`Observer<TState, TEvent, TEffect>`](runtime.md#observer) | `Automaton` | Transition observer delegate |
| [`Interpreter<TEffect, TEvent>`](runtime.md#interpreter) | `Automaton` | Effect interpreter delegate |
| [`ObserverExtensions`](runtime.md#observerextensions) | `Automaton` | Observer composition (`Then`) |
| [`Decider<TState, TCommand, TEvent, TEffect, TError>`](decider.md) | `Automaton` | Command validation interface |
| [`DecidingRuntime<...>`](decider.md#decidingruntime) | `Automaton` | Command-validating runtime |
| [`Result<TSuccess, TError>`](result.md) | `Automaton` | Discriminated union for error handling |
| [`AutomatonDiagnostics`](diagnostics.md) | `Automaton` | OpenTelemetry tracing |

## Dependency Graph

```text
Automaton<S, E, F>          ← kernel interface (pure)
    │
    ├── Decider<S, C, E, F, Err>    ← extends with Decide + IsTerminal
    │
    └── AutomatonRuntime<A, S, E, F>    ← executes the loop
            │
            ├── DecidingRuntime<D, S, C, E, F, Err>  ← wraps with Handle
            │
            ├── Observer<S, E, F>       ← sees transitions
            │
            └── Interpreter<F, E>       ← converts effects to feedback

Result<T, E>                ← used by Decide, independent of runtime

AutomatonDiagnostics        ← ActivitySource for tracing
```

## Source Files

| File | Contains |
| ---- | -------- |
| `Automaton.cs` | `Automaton<TState, TEvent, TEffect>` |
| `Runtime.cs` | `AutomatonRuntime<...>`, `Observer<...>`, `Interpreter<...>`, `ObserverExtensions` |
| `Decider.cs` | `Decider<...>`, `DecidingRuntime<...>` |
| `Result.cs` | `Result<TSuccess, TError>` |
| `Diagnostics.cs` | `AutomatonDiagnostics` |
