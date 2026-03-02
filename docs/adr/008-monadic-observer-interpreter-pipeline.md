# ADR 008: Monadic Observer and Interpreter Pipeline

## Status

Accepted

## Date

2026-03-02

## Context

The Observer and Interpreter extension points in the Automaton runtime originally returned `ValueTask` and `ValueTask<TEvent[]>` respectively. Errors were handled via exceptions — if an observer or interpreter failed, the exception propagated up through the dispatch chain, potentially leaving the system in an inconsistent state (the transition was already committed but the side effect failed).

Several issues motivated this change:

1. **Exception-driven control flow**: Observers and interpreters that fail during I/O (e.g., persistence, HTTP calls) threw exceptions. This violated the functional programming principle of *errors as values*, making composition difficult and error recovery ad hoc.

2. **Monadic structure was implicit**: The chaining of observers via `Then` was sequential composition, but without a Result type, there was no algebraic structure to reason about. Errors broke the chain via exceptions rather than short-circuiting via values.

3. **No composable error recovery**: There was no way to "catch" an observer error and recover without try/catch. Combinators like `Where`, `Select`, and `Catch` require a return type that carries error information.

### Theoretical Foundation

The Observer and Interpreter pipelines form a **Kleisli category** over the `Result<T, PipelineError>` monad:

- **Objects**: Types `Unit` (for observers) and `TEvent[]` (for interpreters)
- **Morphisms**: Functions `A → ValueTask<Result<B, PipelineError>>`
- **Composition**: `Then` is Kleisli composition (>>=), which short-circuits on `Err`

This is the same algebraic structure as **Railway-Oriented Programming** (Scott Wlaschin, 2014), where the "two tracks" are `Ok` and `Err`, and each pipeline stage either continues on the happy track or diverts to the error track.

The combinator vocabulary maps to standard FP abstractions:

| Combinator | FP Concept | Behavior |
|------------|-----------|----------|
| `Then` | Kleisli composition (>>=) | Sequential, short-circuits on Err |
| `Where` | Guard / filter | Skip when predicate is false |
| `Select` | Contravariant functor (contramap) | Transform input before processing |
| `Catch` | Error recovery | Handle Err, optionally resume |
| `Combine` | Applicative (<*>) | Both run, first error wins |

## Decision

### 1. Observer returns `Result<Unit, PipelineError>`

```csharp
public delegate ValueTask<Result<Unit, PipelineError>> Observer<in TState, in TEvent, in TEffect>(
    TState state, TEvent @event, TEffect effect);
```

The `Unit` type replaces `void` in the generic context — it is a readonly struct with a singleton `Value`, optimized away by the JIT.

### 2. Interpreter returns `Result<TEvent[], PipelineError>`

```csharp
public delegate ValueTask<Result<TEvent[], PipelineError>> Interpreter<in TEffect, TEvent>(TEffect effect);
```

### 3. `PipelineError` is a structured error value

```csharp
public readonly record struct PipelineError(
    string Message,
    string? Source = null,
    Exception? Exception = null);
```

A readonly record struct — zero allocation, value semantics, optional source attribution and exception capture for interop with exception-throwing infrastructure.

### 4. `Dispatch` returns `Result<Unit, PipelineError>`

```csharp
public ValueTask<Result<Unit, PipelineError>> Dispatch(TEvent @event, CancellationToken cancellationToken = default)
```

Callers can inspect the result or discard it. The transition is always committed (it's pure), but the caller learns whether the observer or interpreter pipeline succeeded.

### 5. `DecidingRuntime` bridges pipeline errors to exceptions

The `DecidingRuntime.Handle` method returns `Result<TState, TError>` where `TError` is the *domain* error type. Pipeline errors are *infrastructure* errors that don't fit the domain error channel. When a pipeline error occurs during Handle, it is thrown as `InvalidOperationException` with the `PipelineError.Exception` as inner exception.

This maintains the separation between domain errors (returned as values) and infrastructure errors (exceptional).

### 6. C#/.NET naming conventions for combinators

Combinators use LINQ-style names (`Where`, `Select`, `Then`, `Catch`, `Combine`) rather than Haskell-style names (`filter`, `fmap`, `>>=`, `catchError`, `<*>`), following the principle of least surprise for C# developers.

### 7. `PipelineResult.Ok` is a cached static value

```csharp
public static class PipelineResult
{
    public static readonly ValueTask<Result<Unit, PipelineError>> Ok =
        new(Result<Unit, PipelineError>.Ok(Unit.Value));
}
```

Avoids allocating a new `Result<Unit, PipelineError>` on every observer call. Since `Result` is a readonly struct and `ValueTask` wraps it without heap allocation, this is the zero-alloc happy path.

## Alternatives Considered

### A. Keep exceptions for error handling

The status quo. Rejected because it prevents composable error recovery and makes the pipeline non-algebraic.

### B. Use `Option<PipelineError>` instead of `Result<Unit, PipelineError>`

Would avoid the `Unit` type but loses the monadic structure — `Option` doesn't compose the same way as `Result` and doesn't carry a success value through `Map`/`Bind` chains.

### C. Add `TError` type parameter to Observer/Interpreter delegates

Would allow domain-specific error types in pipelines. Rejected because it adds a type parameter to every Observer and Interpreter instantiation, increasing API surface for little practical benefit. `PipelineError` with its `Source` and `Exception` fields is expressive enough for infrastructure errors.

### D. Partial propagation (errors logged but not returned)

The observer/interpreter would log errors but always return success to the dispatch chain. Rejected because it removes the caller's ability to react to pipeline failures.

## Consequences

### Positive

- **Composable error handling**: Observers and interpreters compose via standard FP combinators with principled error propagation.
- **No exception-driven control flow**: Pipeline failures are values, not exceptions. The hot path avoids try/catch overhead.
- **Zero-alloc happy path preserved**: `PipelineResult.Ok` is cached; `Result` is a readonly struct; `ValueTask` avoids heap allocation for synchronous completions.
- **Structured errors**: `PipelineError` carries message, source, and optional exception — richer than bare exceptions.
- **Railway-oriented programming**: The `Then` combinator short-circuits on error, `Catch` recovers, `Combine` runs both — standard ROP vocabulary.

### Negative

- **More verbose lambda signatures**: Observer lambdas must return `PipelineResult.Ok` instead of `ValueTask.CompletedTask`. Interpreter lambdas must wrap arrays in `Result<TEvent[], PipelineError>.Ok(...)`.
- **Breaking change**: All existing Observer and Interpreter implementations must be updated. This is a library-level breaking change.
- **Domain/infrastructure error split**: `DecidingRuntime` must bridge pipeline errors to exceptions because `Handle` returns `Result<TState, TError>` (domain errors only). This is a conceptual seam that could confuse users who expect all errors as values.

### Neutral

- **136 tests pass**: All existing tests updated and passing.
- **Benchmarks updated**: `BenchDomain.cs` and `AutomatonBenchmarks.cs` updated to new signatures.
- **No performance regression expected**: The Result struct is the same size as the previous return values; the JIT optimizes `readonly struct` returns through registers.

## References

- Wlaschin, S. (2014). *Railway Oriented Programming*. F# for Fun and Profit.
- Chassaing, J. (2021). *The Decider Pattern*. thinkbeforecoding.com.
- Milewski, B. (2014). *Category Theory for Programmers*. Chapter on Kleisli Categories.
- Swierstra, W. (2008). *Data types à la carte*. Journal of Functional Programming, 18(4), 423-436.
