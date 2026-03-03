# Automaton.Patterns

**Automaton.Patterns** extends the core Automaton library with production-grade building blocks for event-sourced systems. While the core package gives you the kernel (`Automaton`, `Decider`, `Runtime`, `Result`), the Patterns package provides the infrastructure layer: async event persistence, aggregate lifecycle management, optimistic concurrency with conflict resolution, read-model projections, and saga (process manager) coordination.

```shell
dotnet add package Automaton.Patterns
```

---

## Architecture

```text
┌─────────────────────────────────────────────────────────────────┐
│  Automaton.Patterns                                             │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  EventSourcing                                           │   │
│  │                                                          │   │
│  │  EventStore<TEvent>          ← append-only persistence   │   │
│  │    └─ InMemoryEventStore     ← test / prototype impl     │   │
│  │  StoredEvent<TEvent>         ← event + metadata envelope │   │
│  │                                                          │   │
│  │  AggregateRunner             ← decide → append cycle     │   │
│  │  ResolvingAggregateRunner    ← + conflict resolution     │   │
│  │  ConflictResolver            ← domain-aware retry        │   │
│  │                                                          │   │
│  │  Projection<TEvent, TRead>   ← read-model fold           │   │
│  │  EventSourcingDiagnostics    ← OpenTelemetry tracing     │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Saga                                                    │   │
│  │                                                          │   │
│  │  Saga<TState, TEvent, TEffect, TParameters>              │   │
│  │  SagaRunner                  ← event-sourced process mgr │   │
│  │  SagaDiagnostics             ← OpenTelemetry tracing     │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Automaton (kernel)          ← dependency                │   │
│  │  Automaton<TState, TEvent, TEffect, TParameters>         │   │
│  │  Decider<TState, TCommand, TEvent, TEffect, TError, TP>  │   │
│  │  Result<TSuccess, TError>                                │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

---

## When to Use Patterns

| Scenario | Package |
| -------- | ------- |
| Learning, prototyping, simple state machines | **Automaton** (core) |
| In-memory MVU, Actor, or custom runtimes | **Automaton** (core) |
| Production event sourcing with persistence | **Automaton.Patterns** |
| Optimistic concurrency with automatic retry | **Automaton.Patterns** |
| Read-model projections from event streams | **Automaton.Patterns** |
| Cross-aggregate process management (sagas) | **Automaton.Patterns** |

---

## Event Store

### The Abstraction

`EventStore<TEvent>` is a minimal async interface for event persistence. It defines two operations — **append** and **load** — with optimistic concurrency enforced via expected version numbers.

```csharp
public interface EventStore<TEvent>
{
    ValueTask<IReadOnlyList<StoredEvent<TEvent>>> AppendAsync(
        string streamId,
        IReadOnlyList<TEvent> events,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredEvent<TEvent>>> LoadAsync(
        string streamId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredEvent<TEvent>>> LoadAsync(
        string streamId,
        long afterVersion,
        CancellationToken cancellationToken = default);
}
```

**Design choices:**

- **`ValueTask`** — avoids heap allocation when the implementation completes synchronously (e.g., in-memory stores).
- **Append-only** — events are immutable facts; there is no update or delete.
- **Stream partitioned** — each aggregate instance gets its own stream, identified by a string key (e.g., `"Counter-42"`).
- **Optimistic concurrency** — if the expected version doesn't match the actual stream version, `AppendAsync` throws `ConcurrencyException`.
- **`LoadAsync(streamId, afterVersion)`** — enables incremental catch-up for projections and sagas without replaying the entire stream.

### StoredEvent

Every persisted event is wrapped in a `StoredEvent<TEvent>` envelope carrying metadata:

```csharp
public readonly record struct StoredEvent<TEvent>(
    long SequenceNumber,    // 1-based, monotonically increasing within the stream
    TEvent Event,           // the domain event payload
    DateTimeOffset Timestamp);  // when the event was appended
```

This is a `readonly record struct` to avoid per-event heap allocation — event streams can contain millions of events.

### ConcurrencyException

Thrown when the expected version doesn't match the actual stream version:

```csharp
public sealed class ConcurrencyException : Exception
{
    public string StreamId { get; }
    public long ExpectedVersion { get; }
    public long ActualVersion { get; }
}
```

### InMemoryEventStore

A thread-safe, in-memory implementation for testing and prototyping:

```csharp
var store = new InMemoryEventStore<CounterEvent>();

// Append events to a new stream (expected version = 0)
var stored = await store.AppendAsync("counter-1",
    [new CounterEvent.Incremented(), new CounterEvent.Incremented()],
    expectedVersion: 0);

// Load all events from the stream
var events = await store.LoadAsync("counter-1");
// events.Count == 2, events[0].SequenceNumber == 1

// Append more (expected version must match current count)
await store.AppendAsync("counter-1",
    [new CounterEvent.Decremented()],
    expectedVersion: 2);  // 2 events already in stream

// Concurrent conflict detection
try
{
    await store.AppendAsync("counter-1",
        [new CounterEvent.Incremented()],
        expectedVersion: 2);  // stale! actual version is 3
}
catch (ConcurrencyException ex)
{
    // ex.ExpectedVersion == 2, ex.ActualVersion == 3
}
```

Thread safety is enforced via `System.Threading.Lock`. All operations emit OpenTelemetry spans through `EventSourcingDiagnostics.Source`.

> **Production stores:** Implement `EventStore<TEvent>` against EventStoreDB, Marten/PostgreSQL, CosmosDB, or any durable storage backend. The interface is intentionally minimal to make this straightforward.

---

## AggregateRunner

`AggregateRunner` orchestrates the canonical event-sourcing workflow — the **decide-then-append** pattern:

```text
Command → Decide(state) → Result<Events, Error>
    → Ok:  Transition each event → Append to store → Commit state
    → Err: Return error, state unchanged, nothing persisted
```

### Usage

```csharp
// Define a Decider (see below for full CounterDecider example)
// Then run it as an event-sourced aggregate:

var store = new InMemoryEventStore<CounterEvent>();

// Create a new aggregate
var counter = AggregateRunner<CounterDecider, CounterState,
    CounterCommand, CounterEvent, CounterEffect, CounterError, Unit>
    .Create(store, "counter-1", default);

// Handle commands
var result = await counter.Handle(new CounterCommand.Add(3));
// result.IsOk == true, counter.State == CounterState(3)

var error = await counter.Handle(new CounterCommand.Add(200));
// error.IsErr == true (Overflow), counter.State still CounterState(3)

// Load from event stream (replay)
var reloaded = await AggregateRunner<CounterDecider, CounterState,
    CounterCommand, CounterEvent, CounterEffect, CounterError, Unit>
    .Load(store, "counter-1", default);
// reloaded.State == CounterState(3), reloaded.Version == 3
```

### Create vs Load

| Method | Use case |
|--------|----------|
| `Create(store, streamId, parameters)` | New aggregate — starts from `Init(parameters)` with version 0 |
| `Load(store, streamId, parameters)` | Existing aggregate — replays all stored events through `Transition` |

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `State` | `TState` | Current aggregate state |
| `Version` | `long` | Number of events persisted to the stream |
| `StreamId` | `string` | Stream identifier |
| `Effects` | `IReadOnlyList<TEffect>` | All effects produced during the aggregate's lifetime |
| `IsTerminal` | `bool` | Whether the aggregate has reached a terminal state |

### Thread Safety

By default, `Handle` calls are serialized via a `SemaphoreSlim` to prevent concurrent reads of the same state leading to conflicting appends. Pass `threadSafe: false` for single-threaded scenarios.

### OpenTelemetry Tracing

All operations emit spans via `EventSourcingDiagnostics.Source`:

| Activity | Tags |
|----------|------|
| `Aggregate.Create` | `es.aggregate.type`, `es.stream.id` |
| `Aggregate.Load` | `es.aggregate.type`, `es.stream.id`, `es.event.count`, `es.version` |
| `Aggregate.Handle` | `es.aggregate.type`, `es.stream.id`, `es.command.type`, `es.result`, `es.event.count`, `es.new_version` |
| `Aggregate.Rebuild` | `es.aggregate.type`, `es.stream.id`, `es.event.count`, `es.version` |

Enable tracing in your telemetry pipeline:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(AutomatonDiagnostics.SourceName)     // "Automaton"
        .AddSource(EventSourcingDiagnostics.SourceName)  // "Automaton.Patterns.EventSourcing"
        .AddSource(SagaDiagnostics.SourceName));         // "Automaton.Patterns.Saga"
```

---

## ConflictResolver

`ConflictResolver` extends `Decider` with a single additional static method for domain-aware concurrency conflict resolution:

```csharp
public interface ConflictResolver<TState, TCommand, TEvent, TEffect, TError, TParameters>
    : Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
{
    static abstract Result<TEvent[], ConflictNotResolved> ResolveConflicts(
        TState currentState,       // merged state (their events applied)
        TState projectedState,     // merged + our events (pre-computed by runner)
        TEvent[] ourEvents,        // events we intended to append
        IReadOnlyList<TEvent> theirEvents);  // events committed by another process
}
```

### Key Design Points

- **Pure total function** — returns `Result<TEvent[], ConflictNotResolved>`, never throws for expected outcomes.
- **Pre-computed states** — the runner calls `Transition` for you to produce both `currentState` and `projectedState`. Implementations just inspect the results — no need to fold events manually.
- **Commutativity insight** — many domain operations are commutative (e.g., two independent `Add 3` commands produce the same result regardless of order). For these, `ResolveConflicts` can simply validate invariants on the projected state and return the original events.

### ConflictNotResolved

Represents an irreconcilable conflict as a lightweight value type (no stack trace capture):

```csharp
public readonly record struct ConflictNotResolved(string Reason);
```

### Example: Counter with Commutative Resolution

```csharp
public class CounterDecider
    : ConflictResolver<CounterState, CounterCommand, CounterEvent,
                        CounterEffect, CounterError, Unit>
{
    public const int MaxCount = 100;

    public static (CounterState, CounterEffect) Init(Unit _) =>
        (new CounterState(0), new CounterEffect.None());

    public static Result<CounterEvent[], CounterError> Decide(
        CounterState state, CounterCommand command) =>
        command switch
        {
            CounterCommand.Add(var n) when state.Count + n > MaxCount =>
                Result<CounterEvent[], CounterError>
                    .Err(new CounterError.Overflow(state.Count, n, MaxCount)),

            CounterCommand.Add(var n) when n >= 0 =>
                Result<CounterEvent[], CounterError>
                    .Ok(Enumerable.Repeat<CounterEvent>(
                        new CounterEvent.Incremented(), n).ToArray()),

            CounterCommand.Reset when state.Count is 0 =>
                Result<CounterEvent[], CounterError>
                    .Err(new CounterError.AlreadyAtZero()),

            CounterCommand.Reset =>
                Result<CounterEvent[], CounterError>
                    .Ok([new CounterEvent.WasReset()]),

            // ... other cases
        };

    public static (CounterState, CounterEffect) Transition(
        CounterState state, CounterEvent @event) =>
        @event switch
        {
            CounterEvent.Incremented =>
                (state with { Count = state.Count + 1 }, new CounterEffect.None()),
            CounterEvent.Decremented =>
                (state with { Count = state.Count - 1 }, new CounterEffect.None()),
            CounterEvent.WasReset =>
                (new CounterState(0), new CounterEffect.Log($"Reset from {state.Count}")),
        };

    public static Result<CounterEvent[], ConflictNotResolved> ResolveConflicts(
        CounterState currentState,
        CounterState projectedState,
        CounterEvent[] ourEvents,
        IReadOnlyList<CounterEvent> theirEvents)
    {
        // Reset is non-commutative — cannot merge
        foreach (var e in ourEvents)
            if (e is CounterEvent.WasReset)
                return Result<CounterEvent[], ConflictNotResolved>.Err(
                    new ConflictNotResolved("Reset conflicts with concurrent changes."));

        foreach (var e in theirEvents)
            if (e is CounterEvent.WasReset)
                return Result<CounterEvent[], ConflictNotResolved>.Err(
                    new ConflictNotResolved("Cannot apply changes after concurrent reset."));

        // Increments/decrements are commutative — just validate invariants
        return projectedState.Count switch
        {
            > MaxCount => Result<CounterEvent[], ConflictNotResolved>.Err(
                new ConflictNotResolved($"Count {projectedState.Count} exceeds max.")),
            < 0 => Result<CounterEvent[], ConflictNotResolved>.Err(
                new ConflictNotResolved($"Count {projectedState.Count} below zero.")),
            _ => Result<CounterEvent[], ConflictNotResolved>.Ok(ourEvents)
        };
    }
}
```

---

## ResolvingAggregateRunner

`ResolvingAggregateRunner` is an `AggregateRunner` that automatically catches `ConcurrencyException` and retries via the `ConflictResolver` interface. No manual retry logic needed.

### Resolution Loop

```text
1. Decide(state, command) → events
2. Try AppendAsync(events, expectedVersion)
   ├─ Success → commit state, done
   └─ ConcurrencyException →
       3. LoadAsync(streamId, afterVersion: ourVersion) → theirEvents
       4. Transition(state, theirEvents...) → mergedState
       5. Transition(mergedState, ourEvents...) → projectedState
       6. ResolveConflicts(mergedState, projectedState, ourEvents, theirEvents)
          ├─ Ok(resolvedEvents) → go to step 2 with new events & version
          └─ Err(ConflictNotResolved) → throw ConcurrencyException
7. Max 3 retries, then throw ConcurrencyException
```

### Example

```csharp
var store = new InMemoryEventStore<CounterEvent>();

var counter = ResolvingAggregateRunner<CounterDecider, CounterState,
    CounterCommand, CounterEvent, CounterEffect, CounterError, Unit>
    .Create(store, "counter-1", default);

// Even under concurrent writes, commutative operations resolve automatically
var result = await counter.Handle(new CounterCommand.Add(5));
```

The API is identical to `AggregateRunner` — same `Create`, `Load`, `Handle`, `Rebuild`, same properties. The only difference is the `where TDecider : ConflictResolver<...>` constraint and the automatic retry behavior on concurrency conflicts.

---

## Projection

A `Projection<TEvent, TReadModel>` builds a read-optimized view by folding over an event stream — a left fold with a different accumulator than the aggregate state.

### Projection Example

```csharp
// Define a read model
public record DashboardStats(int TotalIncrements, int TotalResets);

// Create a projection with an initial value and a fold function
var projection = new Projection<CounterEvent, DashboardStats>(
    initial: new DashboardStats(0, 0),
    apply: (stats, @event) => @event switch
    {
        CounterEvent.Incremented => stats with
            { TotalIncrements = stats.TotalIncrements + 1 },
        CounterEvent.WasReset => stats with
            { TotalResets = stats.TotalResets + 1 },
        _ => stats
    });

// Project all events from a stream
var store = new InMemoryEventStore<CounterEvent>();
await projection.Project(store, "counter-1");
// projection.ReadModel == DashboardStats(5, 1)

// Incremental catch-up (only new events since last projection)
await projection.CatchUp(store, "counter-1");

// Live projection — apply individual events as they arrive
projection.Apply(new CounterEvent.Incremented());
```

### Methods

| Method | Description |
|--------|-------------|
| `Project(store, streamId)` | Load and fold all events from a stream |
| `CatchUp(store, streamId)` | Fold only events after `LastProcessedVersion` |
| `Apply(event)` | Apply a single event (for live/subscription-based projection) |
| `Apply(storedEvent)` | Apply a stored event, updating `LastProcessedVersion` |

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `ReadModel` | `TReadModel` | The current read model |
| `LastProcessedVersion` | `long` | Last processed sequence number (for incremental catch-up) |

---

## Saga

### The Abstraction

A **Saga** (Process Manager) coordinates work across multiple aggregates by reacting to domain events and producing effects. Mathematically, a Saga *is* an Automaton:

```text
transition : (SagaState × DomainEvent) → (SagaState × Effect)
```

The key insight: inputs are domain events from various aggregates, and outputs are commands/effects to dispatch to other aggregates. The saga doesn't validate commands — it reacts to what already happened and decides what to do next.

```csharp
public interface Saga<TState, TEvent, TEffect, TParameters>
    : Automaton<TState, TEvent, TEffect, TParameters>
{
    static virtual bool IsTerminal(TState state) => false;
}
```

### Example: Order Fulfillment

```csharp
public enum OrderSagaState
{
    AwaitingPayment, Shipping, Completed, Cancelled
}

public interface OrderDomainEvent
{
    record struct PaymentReceived(string OrderId, decimal Amount) : OrderDomainEvent;
    record struct OrderShipped(string OrderId, string TrackingNumber) : OrderDomainEvent;
    record struct OrderCancelled(string OrderId, string Reason) : OrderDomainEvent;
}

public interface FulfillmentCommand
{
    record struct None : FulfillmentCommand;
    record struct ShipOrder(string OrderId) : FulfillmentCommand;
    record struct SendConfirmation(string OrderId, string TrackingNumber) : FulfillmentCommand;
    record struct RefundPayment(string OrderId, decimal Amount) : FulfillmentCommand;
}

public class OrderFulfillment
    : Saga<OrderSagaState, OrderDomainEvent, FulfillmentCommand, Unit>
{
    public static (OrderSagaState, FulfillmentCommand) Init(Unit _) =>
        (OrderSagaState.AwaitingPayment, new FulfillmentCommand.None());

    public static (OrderSagaState, FulfillmentCommand) Transition(
        OrderSagaState state, OrderDomainEvent @event) =>
        (state, @event) switch
        {
            (OrderSagaState.AwaitingPayment, OrderDomainEvent.PaymentReceived e) =>
                (OrderSagaState.Shipping,
                 new FulfillmentCommand.ShipOrder(e.OrderId)),

            (OrderSagaState.Shipping, OrderDomainEvent.OrderShipped e) =>
                (OrderSagaState.Completed,
                 new FulfillmentCommand.SendConfirmation(e.OrderId, e.TrackingNumber)),

            (OrderSagaState.Shipping, OrderDomainEvent.OrderCancelled e) =>
                (OrderSagaState.Cancelled,
                 new FulfillmentCommand.RefundPayment(e.OrderId, 0)),

            (OrderSagaState.AwaitingPayment, OrderDomainEvent.OrderCancelled _) =>
                (OrderSagaState.Cancelled, new FulfillmentCommand.None()),

            _ => (state, new FulfillmentCommand.None())
        };

    public static bool IsTerminal(OrderSagaState state) =>
        state is OrderSagaState.Completed or OrderSagaState.Cancelled;
}
```

### SagaRunner

`SagaRunner` runs a Saga as an event-sourced process manager. The saga's own event stream is persisted for durability — on restart, state is rebuilt by replaying through `Transition`.

```csharp
var store = new InMemoryEventStore<OrderDomainEvent>();

var saga = SagaRunner<OrderFulfillment, OrderSagaState,
    OrderDomainEvent, FulfillmentCommand, Unit>
    .Create(store, "saga-order-42", default);

// Feed domain events — saga produces commands for other aggregates
var cmd1 = await saga.Handle(
    new OrderDomainEvent.PaymentReceived("order-42", 99.99m));
// cmd1 == FulfillmentCommand.ShipOrder("order-42")
// Caller dispatches: await shippingAggregate.Handle(cmd1);

var cmd2 = await saga.Handle(
    new OrderDomainEvent.OrderShipped("order-42", "TRACK-123"));
// cmd2 == FulfillmentCommand.SendConfirmation("order-42", "TRACK-123")

// saga.IsTerminal == true (Completed)
// saga.Version == 2 (two events persisted to saga stream)
```

**Effect dispatch:** The runner does **not** dispatch effects. It returns them to the caller, who routes them to the appropriate aggregates. This keeps the saga runtime infrastructure-agnostic.

### SagaRunner API

| Method | Description |
|--------|-------------|
| `Create(store, streamId, parameters)` | New saga from `Init(parameters)` |
| `Load(store, streamId, parameters)` | Reload saga by replaying its event stream |
| `Handle(event)` | Transition + persist + return effect |

| Property | Type | Description |
|----------|------|-------------|
| `State` | `TState` | Current saga progress state |
| `Version` | `long` | Events persisted to the saga's stream |
| `IsTerminal` | `bool` | Whether the saga has completed/cancelled |
| `Effects` | `IReadOnlyList<TEffect>` | All effects produced during the saga's lifetime |

---

## Diagnostics

Both the Event Sourcing and Saga runtimes emit OpenTelemetry-compatible tracing via `System.Diagnostics.ActivitySource`. No external OpenTelemetry packages are required.

| Source | Name |
|--------|------|
| `EventSourcingDiagnostics.SourceName` | `"Automaton.Patterns.EventSourcing"` |
| `SagaDiagnostics.SourceName` | `"Automaton.Patterns.Saga"` |

Register both alongside the kernel's `AutomatonDiagnostics.SourceName` (`"Automaton"`) for full tracing coverage.

---

## Type Hierarchy

```text
Automaton<TState, TEvent, TEffect, TParameters>
├── Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
│   └── ConflictResolver<TState, TCommand, TEvent, TEffect, TError, TParameters>
└── Saga<TState, TEvent, TEffect, TParameters>
```

Every type in Automaton.Patterns is defined in terms of these kernel interfaces. The entire system composes through **static interface methods** — no reflection, no runtime dispatch, no base classes.

---

## See Also

- [The Kernel](../concepts/the-kernel.md) — the foundations Patterns builds on
- [The Decider](../concepts/the-decider.md) — AggregateRunner runs a Decider with persistence
- [Event-Sourced Aggregate Tutorial](../tutorials/03-event-sourced-aggregate.md) — step-by-step walkthrough
- [API Reference: Automaton](../reference/automaton.md) — kernel interface reference
- [API Reference: Decider](../reference/decider.md) — command validation reference
