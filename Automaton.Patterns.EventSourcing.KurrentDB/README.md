# Automaton.Patterns.EventSourcing.KurrentDB

KurrentDB (formerly EventStoreDB) implementation of the Automaton `EventStore<TEvent>` abstraction.

## Features

- **Durable event persistence** — Append-only event streams backed by KurrentDB
- **Optimistic concurrency** — Version-checked appends with automatic conflict detection
- **Delegate-based serialization** — No framework coupling; bring your own serializer
- **OpenTelemetry tracing** — Full instrumentation via `System.Diagnostics.ActivitySource`
- **Zero-allocation hot path** — `PoolingAsyncValueTaskMethodBuilder` for async methods

## Installation

```bash
dotnet add package Automaton.Patterns.EventSourcing.KurrentDB
```

## Quick Start

```csharp
using Automaton.Patterns.EventSourcing;
using Automaton.Patterns.EventSourcing.KurrentDB;
using KurrentDB.Client;

// 1. Connect to KurrentDB
var settings = KurrentDBClientSettings.Create("esdb://localhost:2113?tls=false");
var client = new KurrentDBClient(settings);

// 2. Define serialization delegates
var serialize = (MyEvent e) =>
{
    var type = e.GetType().Name;
    var data = JsonSerializer.SerializeToUtf8Bytes(e, jsonOptions);
    return (type, (ReadOnlyMemory<byte>)data);
};

var deserialize = (string type, ReadOnlyMemory<byte> data) =>
    (MyEvent)JsonSerializer.Deserialize(data.Span, typeMap[type], jsonOptions)!;

// 3. Create the event store
EventStore<MyEvent> store = new KurrentDBEventStore<MyEvent>(
    client, serialize, deserialize);

// 4. Use with AggregateRunner
var runner = AggregateRunner.Create<MyDecider, ...>(store, "my-stream-1");
await runner.Handle(new MyCommand());
```

## Serialization

The store is completely decoupled from serialization. You provide two delegates:

| Delegate      | Signature                                                      | Purpose                    |
| ------------- | -------------------------------------------------------------- | -------------------------- |
| `serialize`   | `Func<TEvent, (string EventType, ReadOnlyMemory<byte> Data)>`  | Domain event → wire format |
| `deserialize` | `Func<string, ReadOnlyMemory<byte>, TEvent>`                   | Wire format → domain event |

This means you can use:

- **System.Text.Json** with `[JsonDerivedType]` for polymorphic serialization
- **MessagePack** for compact binary format
- **Protobuf** for schema-driven serialization
- Any custom format

## Version Mapping

| Concept                     | Automaton              | KurrentDB                                            |
| --------------------------- | ---------------------- | ---------------------------------------------------- |
| First event position        | `SequenceNumber = 1`   | `StreamRevision = 0`                                 |
| New stream expected version | `expectedVersion = 0`  | `StreamState.NoStream`                               |
| Stream with N events        | `expectedVersion = N`  | `StreamRevision(N-1)`                                |
| Concurrency conflict        | `ConcurrencyException` | `WrongExpectedVersionException`                      |
| Empty/missing stream        | Returns `[]`           | `StreamNotFoundException` / `ReadState.StreamNotFound` |

## Tracing

All operations emit OpenTelemetry spans via the `Automaton.Patterns.EventSourcing` activity source:

| Span Name          | Tags                                                              |
| ------------------ | ----------------------------------------------------------------- |
| `KurrentDB.Append` | `es.stream.id`, `es.event.count`, `es.expected_version`, `es.new_version` |
| `KurrentDB.Load`      | `es.stream.id`, `es.event.count`                               |
| `KurrentDB.LoadAfter` | `es.stream.id`, `es.after_version`, `es.event.count`           |

Register the source in your telemetry pipeline:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(EventSourcingDiagnostics.SourceName));
```

## Requirements

- .NET 10.0+
- KurrentDB server (v24.10+ recommended)
- KurrentDB.Client 1.3.0+
