# Tutorial 03: Building an Event-Sourced Aggregate

Build a command-driven thermostat aggregate with an event store, replay, projections, and terminal state.

## What You'll Learn

- How Event Sourcing maps to the Automaton kernel
- How to add **commands** and **validation** to the thermostat from Tutorial 01
- How to build an in-memory event store
- How to implement the decide-then-append pattern
- How to rebuild state from an event stream (replay)
- How to build projections (read models) from events
- How terminal state prevents further commands
- How effects are recorded for later processing

## Prerequisites

Complete [Tutorial 01: Getting Started](01-getting-started.md). You should understand the thermostat domain — state, events, and effects. We'll extend it here with commands, errors, and the Decider pattern.

## Event Sourcing in 30 Seconds

Event Sourcing stores **events** (facts) instead of current state. The current state is always derived by replaying events through a transition function:

```text
state = events.Aggregate(initialState, transition)
```

That's a left fold — exactly what the Automaton kernel does.

| Traditional CRUD | Event Sourcing |
| ---------------- | -------------- |
| Store current state | Store events |
| UPDATE row | APPEND event |
| State is mutable | Events are immutable facts |
| History is lost | History is preserved |
| One view of the data | Many projections from the same stream |

## From Automaton to Decider

In Tutorial 01, the thermostat received events directly. But Event Sourcing is fundamentally **command-driven**: user intent arrives as commands, gets validated, and only then produces events.

The **Decider** extends the kernel with two additional concepts:

| Automaton (Tutorial 01) | Decider (this tutorial) |
| ----------------------- | ---------------------- |
| Events arrive directly | Commands arrive, produce events |
| No validation | `Decide(state, command)` validates |
| Always succeeds | Can reject with typed errors |
| `Transition` only | `Decide` + `Transition` |

## Step 1: Add Commands and Errors

We keep the same state, events, and effects from Tutorial 01, and add commands (user intent) and errors (validation failures):

```csharp
// Commands: what the user/sensor wants to do
public interface ThermostatCommand
{
    record struct RecordReading(decimal Temperature) : ThermostatCommand;
    record struct SetTarget(decimal Target) : ThermostatCommand;
    record struct Shutdown : ThermostatCommand;
}

// Errors: why a command was rejected
public interface ThermostatError
{
    record struct InvalidTarget(decimal Target, decimal Min, decimal Max) : ThermostatError;
    record struct SystemInactive : ThermostatError;
    record struct AlreadyShutdown : ThermostatError;
}
```

And extend the state with an `Active` flag for terminal state:

```csharp
public record ThermostatState(
    decimal CurrentTemp,
    decimal TargetTemp,
    bool Heating,
    bool Active);   // ← new: can the thermostat accept commands?
```

## Step 2: Implement the Decider

The Decider separates **what should happen** (`Decide`) from **how it happens** (`Transition`):

```csharp
public class Thermostat
    : Decider<ThermostatState, ThermostatCommand, ThermostatEvent, ThermostatEffect, ThermostatError>
{
    public const decimal MinTarget = 5.0m;
    public const decimal MaxTarget = 40.0m;
    public const decimal AlertThreshold = 35.0m;

    public static (ThermostatState State, ThermostatEffect Effect) Init() =>
        (new ThermostatState(20.0m, 22.0m, Heating: false, Active: true),
         new ThermostatEffect.None());

    public static Result<ThermostatEvent[], ThermostatError> Decide(
        ThermostatState state, ThermostatCommand command) =>
        command switch
        {
            // Shutdown when already inactive → specific error
            ThermostatCommand.Shutdown when !state.Active =>
                Result<ThermostatEvent[], ThermostatError>
                    .Err(new ThermostatError.AlreadyShutdown()),

            // All other commands when inactive → rejected
            _ when !state.Active =>
                Result<ThermostatEvent[], ThermostatError>
                    .Err(new ThermostatError.SystemInactive()),

            // Temperature above alert threshold → record + alert (+ heater off if heating)
            ThermostatCommand.RecordReading(var temp) when temp > AlertThreshold =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok(state.Heating
                        ? [new ThermostatEvent.TemperatureRecorded(temp),
                           new ThermostatEvent.HeaterTurnedOff(),
                           new ThermostatEvent.AlertRaised(
                               $"Temperature {temp}°C exceeds alert threshold {AlertThreshold}°C")]
                        : [new ThermostatEvent.TemperatureRecorded(temp),
                           new ThermostatEvent.AlertRaised(
                               $"Temperature {temp}°C exceeds alert threshold {AlertThreshold}°C")]),

            // Below target, heater off → turn on
            ThermostatCommand.RecordReading(var temp) when temp < state.TargetTemp && !state.Heating =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.TemperatureRecorded(temp),
                         new ThermostatEvent.HeaterTurnedOn()]),

            // At or above target, heater on → turn off
            ThermostatCommand.RecordReading(var temp) when temp >= state.TargetTemp && state.Heating =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.TemperatureRecorded(temp),
                         new ThermostatEvent.HeaterTurnedOff()]),

            // Normal reading, no heater change needed
            ThermostatCommand.RecordReading(var temp) =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.TemperatureRecorded(temp)]),

            // Target out of range → validation error
            ThermostatCommand.SetTarget(var target) when target < MinTarget || target > MaxTarget =>
                Result<ThermostatEvent[], ThermostatError>
                    .Err(new ThermostatError.InvalidTarget(target, MinTarget, MaxTarget)),

            // ... remaining SetTarget and Shutdown cases
        };

    public static (ThermostatState, ThermostatEffect) Transition(
        ThermostatState state, ThermostatEvent @event) =>
        @event switch
        {
            ThermostatEvent.TemperatureRecorded(var temp) =>
                (state with { CurrentTemp = temp }, new ThermostatEffect.None()),

            ThermostatEvent.HeaterTurnedOn =>
                (state with { Heating = true }, new ThermostatEffect.ActivateHeater()),

            ThermostatEvent.HeaterTurnedOff =>
                (state with { Heating = false }, new ThermostatEffect.DeactivateHeater()),

            ThermostatEvent.AlertRaised(var msg) =>
                (state, new ThermostatEffect.SendNotification(msg)),

            ThermostatEvent.ShutdownCompleted =>
                (state with { Active = false },
                 new ThermostatEffect.SendNotification("Thermostat shut down")),

            // ...
        };

    public static bool IsTerminal(ThermostatState state) => !state.Active;
}
```

Key design decisions:

- **One command can produce multiple events.** `RecordReading(18)` when the target is 22 and heater is off produces `[TemperatureRecorded(18), HeaterTurnedOn]`.
- **Pattern ordering matters.** The `AlreadyShutdown` check must come before the general `!state.Active` guard — C# switch expressions evaluate top-to-bottom.
- **Effects come from `Transition`, not `Decide`.** `Decide` says "these events happened." `Transition` says "given this event, here's the new state and what to do about it."
- **Terminal state.** Once shut down, `IsTerminal` returns `true` and `Decide` rejects all further commands.

## Step 3: Build the Event Store

Events in a store need metadata — a sequence number and a timestamp:

```csharp
public readonly record struct StoredEvent<TEvent>(
    long SequenceNumber,
    TEvent Event,
    DateTimeOffset Timestamp);
```

The event store has two operations: **append** and **replay**.

```csharp
public sealed class EventStore<TEvent>
{
    private readonly List<StoredEvent<TEvent>> _events = [];
    private long _sequence;

    public IReadOnlyList<StoredEvent<TEvent>> Events => _events;

    public void Append(TEvent @event) =>
        _events.Add(new StoredEvent<TEvent>(
            ++_sequence, @event, DateTimeOffset.UtcNow));

    public TState Replay<TState>(TState seed, Func<TState, TEvent, TState> fold) =>
        _events.Aggregate(seed, (state, stored) => fold(state, stored.Event));
}
```

`Replay` is the left fold — the same mathematical structure as the Automaton runtime.

## Step 4: Build the Aggregate Runner

The aggregate runner implements the **decide → transition → append** pattern:

```csharp
public sealed class AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError>
    where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError>
{
    private TState _state;
    private readonly EventStore<TEvent> _store;
    private readonly List<TEffect> _effects = [];

    public TState State => _state;
    public EventStore<TEvent> Store => _store;
    public IReadOnlyList<TEffect> Effects => _effects;
    public bool IsTerminal => TDecider.IsTerminal(_state);

    public Result<TState, TError> Handle(TCommand command)
    {
        var decided = TDecider.Decide(_state, command);
        if (decided.IsOk)
        {
            var events = decided.Value;

            // Compute transitions in a single pass over the materialized array
            var newState = _state;
            var newEffects = new List<TEffect>();
            for (var i = 0; i < events.Length; i++)
            {
                var (transitioned, effect) = TDecider.Transition(newState, events[i]);
                newState = transitioned;
                newEffects.Add(effect);
            }

            // Append the already-materialized array to the store
            _store.Append(events);

            _state = newState;
            _effects.AddRange(newEffects);
            return Result<TState, TError>.Ok(_state);
        }
        else
        {
            return Result<TState, TError>.Err(decided.Error);
        }
    }
}
```

Key design decisions:

1. **Events are arrays** — `Decide` returns `TEvent[]`, already materialized. No lazy enumerable surprises.
2. **Compute transitions in locals** — If anything fails, the aggregate's state and store are untouched.
3. **Append after all transitions** — The state/store invariant is preserved on partial failure.
4. **`Handle` is synchronous** — `Decide` and `Transition` are pure functions. No async overhead.

## Step 5: Use the Aggregate

```csharp
var aggregate = AggregateRunner<Thermostat, ThermostatState, ThermostatCommand,
    ThermostatEvent, ThermostatEffect, ThermostatError>.Create();

// Room is cold → heater starts
var result = aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));
// result is Ok(ThermostatState { CurrentTemp = 18, TargetTemp = 22, Heating = true, Active = true })

// Two events persisted: TemperatureRecorded(18) + HeaterTurnedOn
Console.WriteLine(aggregate.Store.Events.Count); // 2
```

A single command produced multiple events — that's the Decider in action.

### Command Rejection

```csharp
// Target out of range → validation error, nothing persisted
var invalid = aggregate.Handle(new ThermostatCommand.SetTarget(50.0m));
// invalid is Err(InvalidTarget { Target = 50, Min = 5, Max = 40 })

// State is unchanged
Console.WriteLine(aggregate.State.TargetTemp); // still 22
Console.WriteLine(aggregate.Store.Events.Count); // still 2
```

### Temperature Alerts

```csharp
// Dangerously hot → alert raised
aggregate.Handle(new ThermostatCommand.RecordReading(36.0m));

// Three events: TemperatureRecorded + HeaterTurnedOff + AlertRaised
// Plus a SendNotification effect recorded for later processing
```

### Terminal State

```csharp
// Shutdown → no more commands accepted
aggregate.Handle(new ThermostatCommand.Shutdown());
Console.WriteLine(aggregate.IsTerminal); // True

var rejected = aggregate.Handle(new ThermostatCommand.RecordReading(21.0m));
// rejected is Err(SystemInactive)

var again = aggregate.Handle(new ThermostatCommand.Shutdown());
// again is Err(AlreadyShutdown)
```

## Step 6: Rebuild State from Events (Replay)

The defining feature of Event Sourcing — rebuild current state by replaying:

```csharp
// Add to AggregateRunner:
public TState Rebuild()
{
    var (seed, _) = TDecider.Init();
    _state = _store.Replay(seed, (s, e) => TDecider.Transition(s, e).State);
    return _state;
}
```

```csharp
aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));   // heater on
aggregate.Handle(new ThermostatCommand.SetTarget(25.0m));       // raise target
aggregate.Handle(new ThermostatCommand.RecordReading(26.0m));   // heater off

var rebuilt = aggregate.Rebuild();
// rebuilt == aggregate.State
// CurrentTemp = 26, TargetTemp = 25, Heating = false, Active = true
```

## Step 7: Hydrate from an Existing Store

When loading events from a database, you hydrate without re-validating — stored events are already validated facts:

```csharp
// Simulate loading from a database
var store = new EventStore<ThermostatEvent>();
store.Append(new ThermostatEvent.TemperatureRecorded(18.0m));
store.Append(new ThermostatEvent.HeaterTurnedOn());
store.Append(new ThermostatEvent.TemperatureRecorded(23.0m));
store.Append(new ThermostatEvent.HeaterTurnedOff());

var aggregate = AggregateRunner<Thermostat, ThermostatState, ThermostatCommand,
    ThermostatEvent, ThermostatEffect, ThermostatError>.FromStore(store);

Console.WriteLine(aggregate.State.CurrentTemp); // 23
Console.WriteLine(aggregate.State.Heating);     // False

// Continue processing new commands
aggregate.Handle(new ThermostatCommand.RecordReading(19.0m));
Console.WriteLine(aggregate.Store.Events.Count); // 6 (4 hydrated + 2 new)
```

## Step 8: Build Projections

Projections build read-optimized views from the event stream. They answer questions the aggregate state can't:

```csharp
public sealed class Projection<TEvent, TReadModel>(
    TReadModel initial,
    Func<TReadModel, TEvent, TReadModel> apply)
{
    public TReadModel ReadModel { get; private set; } = initial;

    public TReadModel Project(EventStore<TEvent> store)
    {
        ReadModel = store.Replay(ReadModel, apply);
        return ReadModel;
    }
}
```

### Temperature History

The aggregate state only remembers the *latest* temperature. A projection remembers them all:

```csharp
aggregate.Handle(new ThermostatCommand.RecordReading(18.0m));
aggregate.Handle(new ThermostatCommand.RecordReading(19.5m));
aggregate.Handle(new ThermostatCommand.RecordReading(21.0m));
aggregate.Handle(new ThermostatCommand.RecordReading(22.5m));

var tempHistory = new Projection<ThermostatEvent, List<decimal>>(
    initial: [],
    apply: (history, @event) =>
    {
        if (@event is ThermostatEvent.TemperatureRecorded(var temp))
            history.Add(temp);
        return history;
    });

var readings = tempHistory.Project(aggregate.Store);
// [18.0, 19.5, 21.0, 22.5]

// State only knows the latest
Console.WriteLine(aggregate.State.CurrentTemp); // 22.5
```

### Heater Duty Cycle

How many times has the heater cycled on and off?

```csharp
var dutyCycle = new Projection<ThermostatEvent, (int OnCount, int OffCount)>(
    initial: (0, 0),
    apply: (counts, @event) => @event switch
    {
        ThermostatEvent.HeaterTurnedOn => (counts.OnCount + 1, counts.OffCount),
        ThermostatEvent.HeaterTurnedOff => (counts.OnCount, counts.OffCount + 1),
        _ => counts
    });

var cycles = dutyCycle.Project(aggregate.Store);
// cycles.OnCount == 2, cycles.OffCount == 2

// State only knows current heating status (true/false)
```

### Alert Log

Track all alerts — the aggregate state doesn't remember alert history:

```csharp
var alertLog = new Projection<ThermostatEvent, List<string>>(
    initial: [],
    apply: (log, @event) =>
    {
        if (@event is ThermostatEvent.AlertRaised(var message))
            log.Add(message);
        return log;
    });

var alerts = alertLog.Project(aggregate.Store);
// ["Temperature 36°C exceeds alert threshold 35°C", ...]
```

### Audit Log

A complete record of everything that happened:

```csharp
var auditLog = new Projection<ThermostatEvent, List<string>>(
    initial: [],
    apply: (log, @event) =>
    {
        log.Add(@event.GetType().Name);
        return log;
    });

var log = auditLog.Project(aggregate.Store);
// ["TemperatureRecorded", "HeaterTurnedOn", "TargetSet",
//  "HeaterTurnedOff", "ShutdownCompleted"]
```

## Step 9: Record Effects

Effects from transitions are collected by the aggregate — process them later:

```csharp
aggregate.Handle(new ThermostatCommand.RecordReading(18.0m)); // ActivateHeater
aggregate.Handle(new ThermostatCommand.RecordReading(36.0m)); // DeactivateHeater + SendNotification

// Effects are recorded, not executed
Assert.Contains(aggregate.Effects, e => e is ThermostatEffect.ActivateHeater);
Assert.Contains(aggregate.Effects, e => e is ThermostatEffect.DeactivateHeater);
Assert.Contains(aggregate.Effects, e => e is ThermostatEffect.SendNotification);
```

In Event Sourcing, effects aren't executed inline (unlike the MVU runtime). They're collected for later processing — maybe by an outbox, a message bus, or an effect handler. This keeps the aggregate pure and replayable.

## Why This Works

Event Sourcing is just the Automaton kernel with specific wiring:

| Component | Automaton | Event Sourcing |
| --------- | --------- | -------------- |
| State evolution | `Transition(state, event)` | Same — it's a left fold |
| Input validation | N/A | `Decide(state, command)` validates |
| Side effects | Interpreter executes effects | Effects are recorded for later processing |
| Observation | Observer sees transitions | Observer appends events to store |
| Terminal state | N/A | `IsTerminal` prevents further commands |

The same thermostat `Transition` function from Tutorial 01 drives the event-sourced aggregate here. The Decider adds commands, validation, and errors on top.

## What's Next

- **[Actor System](04-actor-system.md)** — Process sensor readings from a mailbox
- **[Command Validation](05-command-validation.md)** — Deep dive into the Decider pattern and Result type
- **[Observability](06-observability.md)** — Add distributed tracing to your aggregate
