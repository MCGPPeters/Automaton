# Automaton.Patterns

Production-ready **Event Sourcing** and **Saga** (Process Manager) runtimes built on the [Automaton](https://github.com/MCGPPeters/Automaton) Mealy machine kernel.

## Event Sourcing

Event Sourcing persists every state change as an immutable event. The current state is reconstructed by replaying the event stream through the automaton's transition function — a left fold:

```
state = events.Aggregate(initial, transition)
```

### Components

| Type | Purpose |
|------|---------|
| `EventStore<TEvent>` | Async abstraction for event persistence |
| `InMemoryEventStore<TEvent>` | In-memory implementation for testing |
| `StoredEvent<TEvent>` | Event envelope with sequence number and timestamp |
| `AggregateRunner<TDecider,...>` | Decide → Transition → Append with tracing and optimistic concurrency |
| `Projection<TEvent,TReadModel>` | Builds read-optimized views from event streams |

### Quick Start

```csharp
// Create an aggregate from your Decider
var store = new InMemoryEventStore<MyEvent>();
var aggregate = await AggregateRunner<MyDecider, MyState, MyCommand,
    MyEvent, MyEffect, MyError, Unit>.Create(store, streamId, default);

// Handle commands — decide, transition, persist
var result = await aggregate.Handle(new MyCommand.DoSomething());

// Build read models from the event stream
var projection = new Projection<MyEvent, MyReadModel>(
    initial: new MyReadModel(),
    apply: (model, @event) => /* fold */);

await projection.Project(store);
```

## Saga (Process Manager)

A Saga coordinates work across multiple aggregates by reacting to domain events and producing commands. It is modeled as a Mealy machine:

```
transition : (SagaState × DomainEvent) → (SagaState × SagaEffect)
```

### Components

| Type | Purpose |
|------|---------|
| `Saga<TState,TEvent,TEffect,TParameters>` | Interface extending `Automaton` with lifecycle management |
| `SagaRunner<TSaga,...>` | Event-sourced saga runtime with tracing |

### Quick Start

```csharp
// Define a saga as an Automaton
public class OrderFulfillment
    : Saga<OrderSagaState, OrderEvent, OrderCommand, Unit>
{
    public static (OrderSagaState, OrderCommand) Initialize(Unit _) => ...;
    public static (OrderSagaState, OrderCommand) Transition(
        OrderSagaState state, OrderEvent @event) => ...;
    public static bool IsTerminal(OrderSagaState state) =>
        state is OrderSagaState.Completed or OrderSagaState.Cancelled;
}

// Run the saga
var store = new InMemoryEventStore<OrderEvent>();
var saga = await SagaRunner<OrderFulfillment, OrderSagaState,
    OrderEvent, OrderCommand, Unit>.Create(store, streamId, default);

var effect = await saga.Handle(new OrderEvent.PaymentReceived(...));
// effect contains commands to dispatch to other aggregates
```

## License

Apache-2.0 — see [LICENSE](https://github.com/MCGPPeters/Automaton/blob/main/LICENSE).
