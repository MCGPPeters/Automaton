# ADR-003: Result Type as Algebraic Sum Type

**Status:** Accepted  
**Date:** 2025-06-01  
**Deciders:** Maurice Peters

## Context

The Decider pattern (ADR-004) needs to express that command validation can either succeed (producing events) or fail (producing an error). We need a type that:

1. Makes both outcomes explicit in the type signature
2. Forces callers to handle both cases (no ignored errors)
3. Supports composition (chaining multiple fallible operations)
4. Avoids exceptions for expected domain failures

## Decision

Use `Result<TSuccess, TError>` — an algebraic sum type (coproduct) with two cases:

```csharp
public abstract record Result<TSuccess, TError>
{
    public sealed record Ok(TSuccess Value) : Result<TSuccess, TError>;
    public sealed record Err(TError Error) : Result<TSuccess, TError>;
}
```

With operations:
- `Match` — exhaustive pattern matching (elimination)
- `Map` — transform the success value (functor)
- `Bind` — chain fallible operations (monad)
- `MapError` — transform the error value (bifunctor)

## Mathematical Grounding

### Sum Type (Coproduct)

In type theory, `Result<T, E>` is a **sum type** (also called a tagged union, discriminated union, or coproduct):

$$\text{Result}\langle T, E \rangle \cong T + E$$

Every value is *exactly one of* `Ok(value)` or `Err(error)` — never both, never neither. This is the categorical **coproduct** in the category of types:

$$T + E = \{\ \text{inl}(t) \mid t \in T\ \} \cup \{\ \text{inr}(e) \mid e \in E\ \}$$

Where `inl` = `Ok` (inject left) and `inr` = `Err` (inject right).

### Elimination (Match)

The **elimination principle** for a coproduct requires providing a handler for each case:

$$\text{match} : (T \to R) \to (E \to R) \to (T + E) \to R$$

This is the universal property of the coproduct: to consume a `Result`, you *must* handle both `Ok` and `Err`. The compiler enforces this through exhaustive pattern matching.

```csharp
public TResult Match<TResult>(
    Func<TSuccess, TResult> onOk,
    Func<TError, TResult> onErr)
```

### Functor (Map)

`Result` forms a **functor** in the success type parameter. The `Map` operation applies a function to the success value while leaving errors untouched:

$$\text{map} : (T \to U) \to \text{Result}\langle T, E \rangle \to \text{Result}\langle U, E \rangle$$

Functor laws:
1. **Identity**: `result.Map(x => x)` ≡ `result`
2. **Composition**: `result.Map(f).Map(g)` ≡ `result.Map(x => g(f(x)))`

### Monad (Bind)

`Result` forms a **monad** in the success type parameter. `Bind` (also called `flatMap` or `>>=`) chains fallible operations:

$$\text{bind} : (T \to \text{Result}\langle U, E \rangle) \to \text{Result}\langle T, E \rangle \to \text{Result}\langle U, E \rangle$$

Monad laws:
1. **Left identity**: `new Ok(x).Bind(f)` ≡ `f(x)`
2. **Right identity**: `result.Bind(x => new Ok(x))` ≡ `result`
3. **Associativity**: `result.Bind(f).Bind(g)` ≡ `result.Bind(x => f(x).Bind(g))`

This gives us **railway-oriented programming**: a chain of `Bind` calls short-circuits on the first error, propagating it to the end. The "happy path" reads linearly while errors accumulate automatically.

### Bifunctor (MapError)

`Result` is also a **bifunctor** — functorial in *both* type parameters:

$$\text{bimap} : (T \to U) \to (E \to F) \to \text{Result}\langle T, E \rangle \to \text{Result}\langle U, F \rangle$$

`MapError` is the right component of the bifunctor, allowing error type transformation without affecting the success path.

### Why Not Exceptions?

Exceptions break the mathematical properties above:

| Property | Result | Exceptions |
|----------|--------|------------|
| **Visible in type signature** | ✅ `Result<T, E>` | ❌ Invisible (except checked exceptions in Java) |
| **Composable** | ✅ Map, Bind, Match | ❌ try/catch is not composable |
| **Total function** | ✅ Always returns | ❌ Throws bypass return |
| **Referential transparency** | ✅ Pure | ❌ Side effect (stack unwinding) |
| **Exhaustive handling** | ✅ Match forces both cases | ❌ Catch is optional |

Exceptions are reserved for *programmer bugs* and *unrecoverable infrastructure failures*. Domain validation errors are expected outcomes and belong in the type system.

### Relationship to Either

`Result<T, E>` is isomorphic to Haskell's `Either E T` (note the parameter order convention differs). In category theory, both are the coproduct in **Hask** (the category of Haskell types and functions).

## Consequences

### Positive

- **Errors are values** — domain errors are first-class data, not exceptional control flow.
- **Exhaustive matching** — impossible to forget to handle the error case.
- **Composable** — `Map`, `Bind`, `MapError` enable clean pipelines.
- **Self-documenting** — `Result<TState, TError>` in the signature tells the reader exactly what can happen.

### Negative

- **Verbosity** — `new Result<T, E>.Ok(value)` is more verbose than just returning `value`. (Mitigated by pattern matching and potential future C# union types.)
- **No stack trace** — error values don't carry stack traces. (By design — domain errors are not infrastructure failures.)
- **Nesting** — `Result<Result<T, E1>, E2>` requires flattening. (Use `Bind` to compose.)

### Neutral

- C# does not have built-in discriminated unions (yet). The `abstract record` + `sealed record` pattern is the closest approximation.
- Future C# versions may add native union types, which would simplify the encoding.

## References

- Wadler, P. (1995). "Monads for functional programming." *Advanced Functional Programming*.
- Wlaschin, S. (2013). "Railway Oriented Programming." *F# for Fun and Profit*.
- Milewski, B. (2014). *Category Theory for Programmers*. Chapter 8: Functors.
