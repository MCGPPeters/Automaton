# ADR-013: Non-Generic Command Design

## Status

**Accepted** — 2026-03-06

## Context

During Abies development, we investigated whether `Command` should be generic
(`Command<TMsg>`) to provide compile-time guarantees that a command can only
produce messages of the type the calling component understands. This is how
Elm's `Cmd msg` works — the type parameter enables `Cmd.map : (a → b) → Cmd a → Cmd b`
for composing child programs into parent programs.

We explored six encoding strategies for `Command<TMsg>` in C#:

1. **CPS/Church-Encoded Existential** (Mitchell & Plotkin, 1988)
2. **Functor-Based Command with `Cmd.map`** (Elm's approach)
3. **Continuation-Based Command** (Free Monad lite)
4. **Existential Package via Generic Interface**
5. **Tagless Final** (Carette, Kiselyov & Shan, 2009)
6. **Lambdaba `Data` + Constrained Monoid** (Yallop & White, 2014)

We also investigated whether the `Data<TypeConstructor, A>` pattern from
[Lambdaba](https://github.com/Lambdaba/Lambdaba) could help. It solves HKT
polymorphism (Functor/Monad abstraction) but not existential quantification,
which is what a heterogeneous `Batch` requires (`∃ msg. Command msg`).

## Decision

Keep `Command` as a non-generic marker interface.

## Rationale

### Type safety is vacuous in the current architecture

1. **Abies is a single-`Message`-type system.** There is one `Message` interface
   for the entire application. The type parameter would always be `Command<Message>`,
   adding syntax without adding guarantees.

2. **The Interpreter already enforces the contract.** The kernel's
   `Interpreter<Command, Message>` signature constrains the return type to
   `Message[]`. A wrong-type return is already a compile error.

3. **The monoid operations would all be `Command<Message>`.** `None`, `Batch`,
   and all domain commands would carry the same type parameter — zero additional
   information.

### `Command<TMsg>` only helps with nested program composition via `Cmd.map`

Analysis of [rtfeldman/elm-spa-example](https://github.com/rtfeldman/elm-spa-example)
(the canonical Elm Conduit implementation) showed:

- **99% of reusable UI** is pure view functions (`Model → Html msg`) parameterized
  by `Func<Message>`. No `Cmd.map` needed.
- **1 exception** (`Article.Feed`) has its own Model/Msg/Cmd. Even rtfeldman
  disclaims this as "not normal" and links to a talk explaining why.
- In Abies, the Feed case is handled by nesting state in the app model and
  messages in the app's `Message` interface — no message-type mapping required.

### Approach 3 (Continuation) would eliminate the Interpreter seam

The most promising encoding (`Command<TMsg>(Func<Action<TMsg>, Task> Execute)`)
would make commands opaque closures. This eliminates the Automaton kernel's
`Interpreter<TEffect, TEvent>` extension point — the architectural seam where
the MVU world meets the domain world (e.g., composing with a Decider).

### MVU ↔ Domain composition happens at the Interpreter, not at Command

Composing Abies with a domain Decider is done via the Interpreter:

```text
Message → Transition → Command → Interpreter → Decider → Message[]
```

The Interpreter pattern-matches on `Command` values and bridges to the domain.
Making `Command` generic adds nothing to this flow.

## Consequences

- `Command` stays as `public interface Command` with `None` and `Batch`
- Domain-specific commands are records implementing `Command`
- The Interpreter is the composition seam for MVU ↔ Domain integration
- If nested program composition is ever needed, `Cmd.map` can be added
  incrementally without changing the `Command` interface
- The `Batch` monoid operates freely over heterogeneous command types

## References

- Mitchell & Plotkin, "Abstract types have existential type" (1988)
- Yallop & White, "Lightweight Higher-Kinded Polymorphism" (2014)
- Kennedy & Russo, "Transposing G to C♯" (TCS 2018)
- Granin, "Functional Design and Architecture" (2024)
- [rtfeldman/elm-spa-example](https://github.com/rtfeldman/elm-spa-example) — Conduit in Elm
- [Lambdaba](https://github.com/Lambdaba/Lambdaba) — HKT encoding for .NET
