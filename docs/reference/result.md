# Result&lt;TSuccess, TError&gt;

`namespace Automaton`

A discriminated union representing either a success value or an error. Implemented as a `readonly struct` for zero heap allocation.

## Definition

```csharp
public readonly struct Result<TSuccess, TError>
```

## Construction

### Ok

```csharp
public static Result<TSuccess, TError> Ok(TSuccess value)
```

Creates a successful result containing a value.

### Err

```csharp
public static Result<TSuccess, TError> Err(TError error)
```

Creates a failed result containing an error.

## Properties

| Property | Type | Description |
| -------- | ---- | ----------- |
| `IsOk` | `bool` | Whether this result is a success. |
| `IsErr` | `bool` | Whether this result is an error. |
| `Value` | `TSuccess` | The success value. **Throws** `InvalidOperationException` if this is an Err. |
| `Error` | `TError` | The error value. **Throws** `InvalidOperationException` if this is an Ok. |

## Methods

### Match

```csharp
public TResult Match<TResult>(
    Func<TSuccess, TResult> onOk,
    Func<TError, TResult> onErr)
```

Exhaustive pattern match over both cases. Forces the caller to handle success and error explicitly.

```csharp
var text = result.Match(
    value => $"Success: {value}",
    error => $"Failed: {error}");
```

### Match (Async)

```csharp
public Task<TResult> Match<TResult>(
    Func<TSuccess, Task<TResult>> onOk,
    Func<TError, Task<TResult>> onErr)
```

Async variant for when handlers need to perform I/O.

### Map

```csharp
public Result<TNew, TError> Map<TNew>(Func<TSuccess, TNew> f)
```

Maps a function over the success value (functor). If this is Ok, applies `f` to the value. If this is Err, propagates the error unchanged.

```csharp
Result<int, string>.Ok(21).Map(v => v * 2)   // Ok(42)
Result<int, string>.Err("fail").Map(v => v * 2) // Err("fail")
```

### Bind

```csharp
public Result<TNew, TError> Bind<TNew>(Func<TSuccess, Result<TNew, TError>> f)
```

Chains a function that returns a Result over the success value (monad bind). Enables railway-oriented programming: if this is Ok, applies `f`; if Err, short-circuits.

```csharp
Result<int, string>.Ok(21)
    .Bind(v => v > 50
        ? Result<string, string>.Err("too large")
        : Result<string, string>.Ok($"value: {v * 2}"))
// Ok("value: 42")
```

### MapError

```csharp
public Result<TSuccess, TNew> MapError<TNew>(Func<TError, TNew> f)
```

Maps a function over the error value. If this is Err, applies `f`. If this is Ok, propagates the value unchanged.

```csharp
Result<int, string>.Err("fail").MapError(e => e.Length) // Err(4)
```

### ToString

```csharp
public override string ToString()
```

Returns `"Ok(value)"` or `"Err(error)"`.

## Algebraic Structure

Result is a sum type (coproduct):

```text
Result<T, E> ≅ T + E
```

It supports:

| Operation | Algebraic name | Signature |
| --------- | -------------- | --------- |
| `Map` | Functor | `(T → U) → Result<T, E> → Result<U, E>` |
| `Bind` | Monad | `(T → Result<U, E>) → Result<T, E> → Result<U, E>` |
| `MapError` | Bifunctor (right) | `(E → F) → Result<T, E> → Result<T, F>` |
| `Match` | Eliminator | `(T → R, E → R) → Result<T, E> → R` |

## Implementation Notes

- `Result` is a `readonly struct` — each instance is stack-allocated, eliminating 24 bytes of heap allocation per Decide and Handle call.
- A `bool` discriminator replaces virtual dispatch — no interface overhead.
- The `Value` and `Error` properties throw on invalid access. Prefer `Match` for safe exhaustive handling.

## See Also

- [The Decider](../concepts/the-decider.md) — where Result is used
- [Error Handling Patterns](../guides/error-handling-patterns.md) — Map/Bind/MapError recipes
- [Decider](decider.md) — `Decide` returns `Result<TEvent[], TError>`
