# ADR-012: LINQ Query Syntax for Result, Remove Match Methods

**Status:** Accepted
**Date:** 2026-03-02
**Deciders:** Maurice Peters

## Context

`Result<TSuccess, TError>` had two `Match` methods (sync and async) that provided callback-based exhaustive pattern matching:

```csharp
result.Match(
    value => $"Got {value}",
    error => $"Failed: {error}");
```

Meanwhile, C# has native pattern matching (`switch` expressions, `IsOk`/`IsErr` + `Value`/`Error`) that is both more idiomatic and enforced by the compiler. The `Match` callbacks added API surface without adding capability — they were a Haskell-ism that doesn't carry its weight in modern C#.

At the same time, `Result` had `Map` and `Bind` but lacked LINQ query syntax support. C#'s LINQ comprehension syntax is the language's native monad comprehension — the equivalent of Haskell's `do`-notation. Without `Select` and `SelectMany`, users had to chain `.Bind()` calls:

```csharp
// Without LINQ — nested, hard to read
var result = parseInput(raw)
    .Bind(n => validate(n)
        .Map(v => (n, v)))
    .Map(pair => $"{pair.n}: {pair.v}");
```

## Decision

1. **Remove** both `Match` methods (sync and async)
2. **Add** `Select` as a LINQ alias for `Map`
3. **Add** `SelectMany` with the two-parameter overload required by the C# compiler for multi-`from` queries
4. **Keep** `Map` and `Bind` as the FP-vocabulary names (dual naming)

### Mathematical Grounding

LINQ query syntax is a **monad comprehension** — a notation for composing monadic values that desugars to `Select` (fmap) and `SelectMany` (>>=):

$$\text{do } x \leftarrow m;\ y \leftarrow f(x);\ \text{return } g(x, y)$$

desugars to:

$$m \bind (\lambda x.\ f(x) \bind (\lambda y.\ \text{return } g(x, y)))$$

In C#, this is:

```csharp
from x in m
from y in f(x)
select g(x, y)

// desugars to:
m.SelectMany(x => f(x), (x, y) => g(x, y))
```

The two-parameter `SelectMany` with a result selector is C#'s optimization to avoid intermediate tupling — it fuses the bind and the final projection into a single call.

**Why `Select` and `SelectMany` specifically?** These names are required by the C# compiler for query syntax. They follow the same convention as `IEnumerable<T>`, `IQueryable<T>`, `Task<T>`, and `Nullable<T>`. The compiler pattern-matches on method names, not interfaces — any type with `Select` and `SelectMany` methods automatically supports LINQ syntax.

### Why Remove Match?

| Aspect | `Match` (callbacks) | C# pattern matching |
|--------|--------------------|--------------------|
| **Exhaustiveness** | Runtime (forgets a case = runtime error) | Compile-time (compiler warning) |
| **Allocation** | 2 `Func` delegates per call | Zero allocation |
| **Idiomatic C#** | ❌ Foreign concept | ✅ Native language feature |
| **Debuggability** | Step through lambdas | Step through linear code |

C# is not Haskell. The language has first-class pattern matching. Adding a `Match` method on top of `IsOk`/`IsErr` is redundant API surface that adds no capability.

## Consequences

### Positive

- **LINQ query syntax** — `from ... in ... select ...` works out of the box:

  ```csharp
  var result =
      from order in Order.Create(cmd)
      from payment in ProcessPayment(order)
      select new OrderConfirmed(order.Id, payment.Id);
  ```

- **Dual vocabulary** — FP developers use `Map`/`Bind`, C# developers use `Select`/`SelectMany` or LINQ syntax
- **Smaller API surface** — `Match` removed (2 methods), `Select`/`SelectMany` added (2 methods), net zero
- **Zero-allocation** — `Select`/`SelectMany` are struct methods, no interface dispatch

### Negative

- **Breaking change** — code using `.Match()` must be rewritten to use `IsOk`/`IsErr` or LINQ
- **Dual naming** — `Map` vs `Select` and `Bind` vs `SelectMany` could confuse newcomers (mitigated by XML docs explaining the aliases)

### Neutral

- `SelectMany` uses pattern matching internally (`switch` on the intermediate result) to avoid double-unwrapping
- The `let` keyword in LINQ queries works automatically via `Select` (the compiler desugars `let x = expr` to `.Select(v => (v, x = expr))`)

## References

- Wadler, P. (2007). "Comprehending Monads." *Mathematical Structures in Computer Science*.
- Petricek, T. & Syme, D. (2014). "The F# Computation Expression Zoo." *PADL*.
- Lippert, E. (2008). "Monads, Part One." *Fabulous Adventures in Coding* (MSDN Blog).
- C# Language Specification §12.20 — Query expressions and their translation to method calls.
