# API Reference

Complete type and method documentation for the `Automaton` package.

## Types

| Type | Namespace | Purpose |
| ---- | --------- | ------- |
| [`Automaton<TState, TEvent, TEffect, TParameters>`](automaton.md) | `Automaton` | The kernel interface — Init + Transition |
| [`AutomatonRuntime<TAutomaton, TState, TEvent, TEffect, TParameters>`](runtime.md) | `Automaton` | Thread-safe async runtime |
| [`Observer<TState, TEvent, TEffect>`](runtime.md#observer) | `Automaton` | Transition observer delegate |
| [`Interpreter<TEffect, TEvent>`](runtime.md#interpreter) | `Automaton` | Effect interpreter delegate |
| [`ObserverExtensions`](runtime.md#observerextensions) | `Automaton` | Observer composition (`Then`, `Where`, `Select`, `Catch`, `Combine`) |
| [`InterpreterExtensions`](runtime.md#interpreterextensions) | `Automaton` | Interpreter composition (`Then`, `Where`, `Select`, `Catch`) |
| [`PipelineError`](runtime.md#pipelineerror) | `Automaton` | Structured error from observer/interpreter pipeline |
| [`PipelineResult`](runtime.md#pipelineresult) | `Automaton` | Pre-allocated `Ok` result for zero-alloc happy path |
| [`Unit`](runtime.md#unit) | `Automaton` | Unit type for `Result<Unit, PipelineError>` |
| [`Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>`](decider.md) | `Automaton` | Command validation interface |
| [`DecidingRuntime<...>`](decider.md#decidingruntime) | `Automaton` | Command-validating runtime |
| [`Result<TSuccess, TError>`](result.md) | `Automaton` | Discriminated union for error handling |
| [`AutomatonDiagnostics`](diagnostics.md) | `Automaton` | OpenTelemetry tracing |

## Dependency Graph

```text
Automaton<S, E, F, P>          ← kernel interface (pure)
    │
    ├── Decider<S, C, E, F, Err, P>    ← extends with Decide + IsTerminal
    │
    └── AutomatonRuntime<A, S, E, F, P>    ← executes the loop
            │
            ├── DecidingRuntime<D, S, C, E, F, Err, P>  ← wraps with Handle
            │
            ├── Observer<S, E, F>       ← sees transitions, returns Result<Unit, PipelineError>
            │   └── ObserverExtensions  ← Then, Where, Select, Catch, Combine
            │
            └── Interpreter<F, E>       ← converts effects to feedback, returns Result<E[], PipelineError>
                └── InterpreterExtensions ← Then, Where, Select, Catch

Result<T, E>                ← used by Decide, Observer, Interpreter. LINQ monad (Select, SelectMany).
Unit                        ← success type for effectful operations (replaces void in Result)
PipelineError               ← structured error for Observer/Interpreter pipelines
PipelineResult              ← pre-allocated Ok for zero-alloc happy path

AutomatonDiagnostics        ← ActivitySource for tracing
```

## Source Files

| File | Contains |
| ---- | -------- |
| `Automaton.cs` | `Automaton<TState, TEvent, TEffect, TParameters>` |
| `Runtime.cs` | `AutomatonRuntime<...>`, `Observer<...>`, `Interpreter<...>`, `ObserverExtensions`, `InterpreterExtensions`, `PipelineError`, `PipelineResult`, `Unit` |
| `Decider.cs` | `Decider<...>`, `DecidingRuntime<...>` |
| `Result.cs` | `Result<TSuccess, TError>` |
| `Diagnostics.cs` | `AutomatonDiagnostics` |
