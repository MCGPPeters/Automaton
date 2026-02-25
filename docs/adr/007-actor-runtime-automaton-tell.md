# ADR-007: Actor Runtime — Automaton Constraint with Tell

**Status:** Accepted  
**Date:** 2025-06-01  
**Deciders:** Maurice Peters

## Context

The Actor Model needs a runtime that:

1. Encapsulates state behind a message-passing interface
2. Processes messages sequentially from a mailbox (no shared mutable state)
3. May send messages to other actors or spawn new actors as effects
4. Does not expose a synchronous error channel to the sender

We must decide:
- Should actors use the Automaton constraint (`Tell(message)`) or the Decider constraint (`Handle(command)` with `Result`)?
- How should the mailbox be implemented?
- What is the relationship between actor effects and the interpreter?

## Decision

The Actor runtime uses the **Automaton constraint** with fire-and-forget `Tell`:

```csharp
public sealed class ActorInstance<TAutomaton, TState, TEvent, TEffect>
    where TAutomaton : Automaton<TState, TEvent, TEffect>
```

The actor is accessed exclusively through a typed reference:

```csharp
public sealed class ActorRef<TEvent>
{
    public async ValueTask Tell(TEvent message) =>
        await _writer.WriteAsync(message);
}
```

Key design choices:

1. **Fire-and-forget** — `Tell` enqueues and returns immediately. No response, no error channel.
2. **Channel-based mailbox** — `System.Threading.Channels.Channel<TEvent>` with `SingleReader = true`.
3. **Sequential processing** — one message at a time via `ReadAllAsync`.
4. **Automaton, not Decider** — no `Result` return, no command validation at the Tell boundary.

### Wiring to AutomatonRuntime

```csharp
// Observer: no-op — actor state is internal
Observer<TState, TEvent, TEffect> observer = (_, _, _) => Task.CompletedTask;

// Interpreter: wraps the effect handler with self-reference
Interpreter<TEffect, TEvent> interpreter = async effect =>
{
    await effectHandler(effect, actorRef);
    return [];
};
```

| Extension Point | Actor Wiring | Purpose |
|----------------|--------------|---------|
| **Observer** | No-op | State is private; no external rendering or persistence |
| **Interpreter** | Effect handler with `ActorRef` | Effects can send messages (to self or others) |

## Mathematical Grounding

### The Actor Model (Hewitt, Bishop & Steiger, 1973)

An actor is an entity that, upon receiving a message, can:

1. **Send** a finite number of messages to other actors
2. **Create** a finite number of new actors
3. **Designate** a new behavior for the next message

Formally, an actor's behavior is:

$$\text{behavior} : \text{Message} \to (\text{Behavior}',\; [\text{Message}],\; [\text{Actor}])$$

This maps directly to the Automaton:

| Actor concept | Automaton concept |
|--------------|-------------------|
| Behavior | `TState` |
| Message | `TEvent` |
| New behavior | `TState'` (new state from Transition) |
| Messages to send + actors to create | `TEffect` (interpreted by the effect handler) |
| `behavior(msg)` | `Transition(state, event) → (state', effect)` |

The actor IS a Mealy machine with a mailbox.

### Sequential Processing as Linearizability

Actors process one message at a time. This provides **linearizability** — each message sees the full effect of all previous messages:

$$\text{state}_n = T(\text{state}_{n-1}, \text{message}_n)$$

There are no data races because there is no concurrent access to state. The `Channel<TEvent>` with `SingleReader = true` enforces this at the type level.

This is equivalent to a **serialized** execution model: all message processing is totally ordered, forming a sequence:

$$s_0 \xrightarrow{m_1} s_1 \xrightarrow{m_2} s_2 \xrightarrow{m_3} \ldots$$

### Tell (Fire-and-Forget) vs Ask (Request-Response)

The actor uses the **Tell pattern** (one-way), not the **Ask pattern** (request-response):

| Pattern | Signature | Coupling | Error handling |
|---------|-----------|----------|----------------|
| **Tell** | `Message → ()` | Minimal — sender doesn't wait | None at call site |
| **Ask** | `Request → Task<Response>` | High — sender blocks on response | Caller handles errors |

**Why Tell, not Ask:**

1. **Temporal decoupling** — the sender continues immediately, regardless of how long processing takes
2. **Location transparency** — Tell works the same whether the actor is in-process, on another thread, or on another machine
3. **No deadlocks** — since Tell never blocks, circular message patterns cannot deadlock
4. **Matches the mathematical model** — Hewitt's original actors don't have a synchronous response channel

If response is needed, the actor sends a message back to the caller (callback pattern), preserving the asynchronous, decoupled nature.

### Why Automaton, Not Decider

| Criterion | Tell (Automaton) | Handle (Decider) |
|-----------|-----------------|------------------|
| **Error channel** | None — fire-and-forget | `Result<TState, TError>` returned to caller |
| **Coupling** | Minimal — sender doesn't know/care | High — sender must handle errors |
| **Backpressure** | Via Channel fullness | Via Result processing |
| **Validation** | Inside Transition (errors become state) | In Decide (errors returned) |
| **Hewitt model** | ✅ Matches exactly | ❌ Adds synchronous coupling |

Actors deliberately lack a synchronous error return because:
- The sender has already moved on — it cannot act on the error
- Error handling is the actor's responsibility (supervision, retry, dead-letter)
- Adding a response channel breaks the fire-and-forget semantics that make actors composable

If command validation IS needed for an actor, the validation happens inside `Transition` (same as MVU — errors become state), and the actor can send an error message back to the caller as an effect.

### Mailbox as Buffered Channel (CSP Heritage)

The `Channel<TEvent>` mailbox connects to the **Communicating Sequential Processes** (Hoare, 1978) tradition:

- **Channel** = typed, FIFO, multiple-writer/single-reader
- **Actor** = sequential process reading from its channel
- **Tell** = non-blocking write to the channel

The `UnboundedChannelOptions { SingleReader = true, SingleWriter = false }` configuration optimizes for the actor pattern:
- Single reader: only the actor's processing loop reads
- Multiple writers: any number of `ActorRef` holders can send messages

### Effect Handler with Self-Reference (Recursion)

The interpreter receives a reference to the actor itself:

```csharp
Interpreter<TEffect, TEvent> interpreter = async effect =>
{
    await effectHandler(effect, actorRef);
    return [];
};
```

This enables recursive patterns:
- An actor can send messages to itself (`actorRef.Tell(...)`)
- An actor can spawn child actors that send back to the parent
- Effects are fire-and-forget — the interpreter returns `[]` (no feedback events through the fold)

The self-reference is the actor model's equivalent of the **Y combinator** — it enables recursion without direct self-access.

### Relation to Erlang/OTP

| Erlang/OTP | Automaton Actor |
|------------|----------------|
| `gen_server` | `ActorInstance` |
| `Pid` | `ActorRef<TEvent>` |
| `handle_cast/2` | `Transition` (fire-and-forget) |
| `!` (send) | `Tell` |
| Mailbox | `Channel<TEvent>` |
| `init/1` | `Automaton.Init()` |
| Supervision tree | Not yet implemented (future work) |

The key difference: Erlang actors are dynamically typed (any message), while Automaton actors are statically typed (`TEvent`). This provides compile-time safety at the cost of flexibility.

## Consequences

### Positive

- **No shared mutable state** — each actor owns its state; communication is via messages only.
- **Sequential by default** — no locks, no data races, no concurrency bugs within an actor.
- **Decoupled** — Tell is fire-and-forget; sender and receiver are temporally independent.
- **Typed messages** — `ActorRef<TEvent>` ensures only valid message types are sent.
- **Testable** — `Tell` + `DrainMailbox` + assert on `State` gives deterministic testing.

### Negative

- **No request-response** — callers cannot directly get results. Must use callback patterns.
- **No supervision** — no built-in restart/escalation strategy for failed actors. (Future work.)
- **Unbounded mailbox** — the current implementation uses an unbounded channel. Under load, this can cause memory growth. (Production use should add backpressure.)
- **DrainMailbox is a heuristic** — relies on `Task.Delay` for timing, which is not perfectly deterministic. (Acceptable for testing.)

### Neutral

- Actors can be upgraded to use the Decider internally (validation inside Transition that transitions to an error state) without changing the external `Tell` interface.
- Future work may add supervision trees, clustering, and persistence (event-sourced actors).

## References

- Hewitt, C., Bishop, P., & Steiger, R. (1973). "A Universal Modular ACTOR Formalism for Artificial Intelligence." *IJCAI*.
- Armstrong, J. (2003). *Making reliable distributed systems in the presence of software errors*. PhD thesis, KTH.
- Hoare, C. A. R. (1978). "Communicating sequential processes." *Communications of the ACM*, 21(8), 666–677.
- Agha, G. (1986). *Actors: A Model of Concurrent Computation in Distributed Systems*. MIT Press.
