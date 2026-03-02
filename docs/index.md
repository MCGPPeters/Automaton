# Automaton Documentation

**Write your domain logic once. Run it everywhere.**

Automaton is a minimal, production-hardened kernel for building state machines, MVU runtimes, event-sourced aggregates, and actor systems in .NET — based on the observation that all three are instances of the same mathematical structure: a [Mealy machine](concepts/the-kernel.md).

```text
transition : (State × Event) → (State × Effect)
```

## Where to Start

<table>
<tr>
<td width="50%">

### 🚀 Getting Started

New to Automaton? Start here.

- [**Quick Start**](getting-started/index.md) — Build a counter in 5 minutes
- [**Installation**](getting-started/installation.md) — Prerequisites and project setup

</td>
<td width="50%">

### 💡 Concepts

Understand the ideas behind the library.

- [**The Kernel**](concepts/the-kernel.md) — Mealy machines and effects as data
- [**The Runtime**](concepts/the-runtime.md) — Observer, Interpreter, and the monadic fold
- [**The Decider**](concepts/the-decider.md) — Command validation with Result
- [**Runtimes Compared**](concepts/runtimes-compared.md) — MVU vs ES vs Actor
- [**Glossary**](concepts/glossary.md) — Key terms defined

</td>
</tr>
<tr>
<td>

### 📚 Tutorials

Step-by-step guides that build real systems.

1. [Getting Started](tutorials/01-getting-started.md) — Thermostat with feedback loop
2. [MVU Runtime](tutorials/02-mvu-runtime.md) — Model-View-Update loop
3. [Event-Sourced Aggregate](tutorials/03-event-sourced-aggregate.md) — Persistence and replay
4. [Actor System](tutorials/04-actor-system.md) — Mailbox and fire-and-forget
5. [Command Validation](tutorials/05-command-validation.md) — Decider pattern and Result
6. [Observability](tutorials/06-observability.md) — OpenTelemetry tracing

</td>
<td>

### 🔧 How-To Guides

Solve specific problems.

- [Testing Strategies](guides/testing-strategies.md) — Unit, integration, and property-based
- [Observer Composition](guides/observer-composition.md) — Chain and combine observers
- [Upgrading to Decider](guides/upgrading-to-decider.md) — Add command validation
- [Error Handling Patterns](guides/error-handling-patterns.md) — Result pipelines
- [Building Custom Runtimes](guides/building-custom-runtimes.md) — Roll your own

</td>
</tr>
<tr>
<td>

### 📖 API Reference

Complete type and method documentation.

- [Automaton](reference/automaton.md) — The kernel interface
- [Runtime](reference/runtime.md) — AutomatonRuntime, Observer, Interpreter
- [Decider](reference/decider.md) — Decider, DecidingRuntime
- [Result](reference/result.md) — Result&lt;TSuccess, TError&gt;
- [Diagnostics](reference/diagnostics.md) — AutomatonDiagnostics

</td>
<td>

### 🏗️ Architecture

Design rationale with mathematical grounding.

- [Architecture Decision Records](adr/) — All ADRs with formal analysis
- [Automaton.Patterns](patterns/index.md) — Production patterns *(coming soon)*

</td>
</tr>
</table>

## The Big Idea

Every runtime in this library is built on the **same kernel**:

```csharp
public interface Automaton<TState, TEvent, TEffect>
{
    static abstract (TState State, TEffect Effect) Init();
    static abstract (TState State, TEffect Effect) Transition(TState state, TEvent @event);
}
```

You write your domain logic as a pure transition function. The runtime handles the rest — rendering, persistence, messaging, thread safety, cancellation, tracing — without you changing a single line of domain code.

| Pattern | Observer wiring | Interpreter wiring | Entry point |
|---------|----------------|-------------------|-------------|
| **MVU** | Render the view | Execute effects → feedback events | `Dispatch(event)` |
| **Event Sourcing** | Append to store | No-op | `Handle(command)` |
| **Actor** | No-op | Execute with self-reference | `Tell(message)` |

## Packages

| Package | Description | Status |
|---------|-------------|--------|
| [`Automaton`](https://www.nuget.org/packages/Automaton) | Core kernel, runtime, Decider, Result, diagnostics | ✅ Stable |
| `Automaton.Patterns` | Production Event Sourcing, Saga, ConflictResolver | 🚧 Coming soon |
