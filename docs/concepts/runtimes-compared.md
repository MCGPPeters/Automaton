# Runtimes Compared

The Automaton kernel provides one interface. This page helps you choose the right runtime pattern for your problem.

## The Three Patterns

| | MVU | Event Sourcing | Actor |
| - | --- | -------------- | ----- |
| **Full name** | Model-View-Update | Event Sourcing with Decider | Actor Model |
| **Origin** | Elm Architecture (2012) | DDD/CQRS (2005) | Hewitt (1973) |
| **Entry point** | `Dispatch(event)` | `Handle(command)` | `Tell(message)` |
| **Constraint** | `Automaton` | `Decider` | `Automaton` |
| **Observer** | Render view | Append to store | No-op |
| **Interpreter** | Execute effects → feedback | No-op | Execute with self-ref |
| **Error model** | Errors become state | `Result<Events, Error>` | Errors become state |
| **Persistence** | None (in-memory) | Event store | None (in-memory) |
| **Threading** | Single-threaded UI loop | Thread-safe (semaphore) | Single-reader channel |

## When to Use Each

### MVU — User Interfaces and Interactive Systems

**Use when** you're building a UI, a REPL, a game loop, or any system where a human sees rendered output after each interaction.

**Key characteristics:**
- Events are UI messages (button clicks, keypresses, API responses)
- Every transition renders a new view
- Effects trigger async operations (HTTP calls, timers) that produce feedback events
- The view function is the primary output — state drives the screen

**Examples:** Desktop apps, terminal UIs, game loops, interactive wizards, chatbot conversations.

```csharp
// MVU: Observer renders, Interpreter handles effects
Observer = (state, _, _) => { views.Add(render(state)); return PipelineResult.Ok; };
Interpreter = effect => executeAndReturnFeedbackEvents(effect);
```

### Event Sourcing — Persistent Domain Logic

**Use when** you need an audit trail, temporal queries, or the ability to rebuild state from history. Event Sourcing is fundamentally **command-driven** — commands arrive, get validated, and only the resulting events are persisted.

**Key characteristics:**
- Commands represent user intent; events are persisted facts
- `Decide` validates commands against current state before producing events
- Events are immutable — stored forever in an append-only log
- State is always derivable by replaying events through `Transition`
- Projections build read-optimized views from the event stream

**Examples:** Financial systems (ledger), order management, inventory tracking, compliance-heavy domains, anything requiring complete audit history.

```csharp
// ES: Observer persists events, Interpreter is no-op
Observer = (_, event, _) => { store.Append(event); return PipelineResult.Ok; };
Interpreter = _ => new ValueTask<Result<TEvent[], PipelineError>>(
    Result<TEvent[], PipelineError>.Ok([]));
```

### Actor — Concurrent Isolated Processes

**Use when** you have many independent entities processing messages concurrently, and you want location-transparent messaging without shared memory.

**Key characteristics:**
- Each actor has private state — no shared memory, no locks
- Messages arrive via a mailbox (channel), processed one at a time
- Fire-and-forget (`Tell`) is the primary communication pattern
- Effects can send messages to self or other actors
- Naturally models real-world entities (devices, users, sessions)

**Examples:** IoT device management, chat systems, game entities, session managers, workflow orchestration, any system with many independent stateful entities.

```csharp
// Actor: Observer is no-op, Interpreter executes effects with self-reference
Observer = (_, _, _) => PipelineResult.Ok;
Interpreter = async effect => { await handler(effect, selfRef); return Result<TEvent[], PipelineError>.Ok([]); };
```

## Decision Flowchart

```text
Do you need to persist state changes as events?
├── Yes → Do you need command validation?
│         ├── Yes → Event Sourcing (Decider + EventStore)
│         └── No  → Event Sourcing (Automaton + EventStore observer)
└── No  → Do you have many concurrent independent entities?
          ├── Yes → Actor Model (Channel + ProcessLoop)
          └── No  → Is there a UI or rendered output?
                    ├── Yes → MVU (Render observer + effect interpreter)
                    └── No  → Plain AutomatonRuntime (custom observer + interpreter)
```

## Mixing Patterns

These patterns aren't mutually exclusive. You can combine them:

- **Actor + Event Sourcing** — Each actor persists its events. The actor provides isolation and messaging; ES provides persistence and replay. This is the Akka Persistence / Orleans pattern.

- **MVU + Decider** — Use `DecidingRuntime` with a render observer. Commands get validated before the UI updates. Rejected commands can render error views without corrupting state.

- **Event Sourcing + Projections** — The aggregate handles commands and stores events. Projections (read models) build optimized query views from the same event stream.

## The Proof: Same Transition Function

The same `Counter` domain definition — the same `Initialize` and `Transition` — drives all three patterns. Here's the proof:

```csharp
// All three produce the same final state:
var events = new CounterEvent[] { new Increment(), new Increment(), new Decrement() };
var (seed, _) = Counter.Initialize(default);

var state = events.Aggregate(seed, (s, e) => Counter.Transition(s, e).State);
// state.Count == 1
```

- In MVU, these events come from UI interactions and feedback
- In ES, these events come from validated commands
- In an Actor, these events come from a mailbox channel

The transition function doesn't know or care. It's the same fold.

## Performance Characteristics

| Aspect | MVU | Event Sourcing | Actor |
| ------ | --- | -------------- | ----- |
| **Startup cost** | Instant | O(n) replay if hydrating from store | Instant |
| **Per-operation cost** | Transition + render | Decide + transition + append | Channel read + transition |
| **Memory** | Current state + view history | Current state + event store | Current state per actor |
| **Concurrency model** | Single-threaded | Semaphore-serialized | Single-reader channel |
| **Allocation per op** | View allocation | Event array + store entry | Channel message |

## Next

- [**Tutorial 01**](../tutorials/01-getting-started.md) — build a thermostat with the core runtime
- [**Tutorial 02**](../tutorials/02-mvu-runtime.md) — build an MVU runtime
- [**Tutorial 03**](../tutorials/03-event-sourced-aggregate.md) — build an event-sourced aggregate
- [**Tutorial 04**](../tutorials/04-actor-system.md) — build an actor system
- [**Building Custom Runtimes**](../guides/building-custom-runtimes.md) — roll your own
