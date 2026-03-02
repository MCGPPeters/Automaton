# Tutorial 04: Building an Actor System

Build a mailbox actor with channels, fire-and-forget messaging, and effect callbacks.

## What You'll Learn

- How the Actor Model maps to the Automaton kernel
- How to build a mailbox with `System.Threading.Channels`
- How to implement fire-and-forget messaging (the Tell pattern)
- How to handle effects with self-reference
- How to drain the mailbox and observe actor state

## Prerequisites

Complete [Tutorial 01: Getting Started](01-getting-started.md) first. We'll reuse the same `Counter` automaton.

## The Actor Model in 30 Seconds

An **actor** is an isolated unit of computation that:

1. Has private state (no shared memory)
2. Receives messages via a mailbox
3. Processes messages one at a time (sequential)
4. Can send messages to other actors
5. Can spawn new actors

If you squint, that's exactly the Automaton kernel:

| Actor | Automaton |
| ----- | --------- |
| State | State |
| Message | Event |
| Behavior change | Transition |
| Side effects (send, spawn) | Effect |
| Mailbox | Channel (input queue) |

An Actor is the Automaton with:

- **Observer** = no-op (actor state is internal, not rendered)
- **Interpreter** = execute effects with self-reference (for sending messages to self or others)

## Step 1: Build an Actor Reference

An `ActorRef` is a typed handle for sending messages. It hides the actor's internals:

```csharp
using System.Threading.Channels;

public sealed class ActorRef<TEvent>
{
    private readonly ChannelWriter<TEvent> _writer;
    internal int _sentCount;

    public string Name { get; }

    internal ActorRef(string name, ChannelWriter<TEvent> writer)
    {
        Name = name;
        _writer = writer;
    }

    /// <summary>
    /// Fire-and-forget: sends a message to the actor's mailbox.
    /// </summary>
    public async ValueTask Tell(TEvent message)
    {
        Interlocked.Increment(ref _sentCount);
        await _writer.WriteAsync(message);
    }
}
```

Key design decisions:

- **`Tell`, not `Ask`** — Fire-and-forget is the fundamental actor primitive. Ask (request-response) is built on top.
- **`ValueTask`** — Channel writes are usually synchronous (buffer not full), so `ValueTask` avoids a `Task` allocation.
- **Sent counter** — Used by `DrainMailbox` to know when all messages have been processed.

## Step 2: Build the Actor Instance

```csharp
using Automaton;

public sealed class ActorInstance<TAutomaton, TState, TEvent, TEffect>
    where TAutomaton : Automaton<TState, TEvent, TEffect>
{
    private readonly AutomatonRuntime<TAutomaton, TState, TEvent, TEffect> _core;
    private readonly Channel<TEvent> _mailbox;
    private readonly CancellationTokenSource _cts = new();
    private int _processedCount;

    public TState State => _core.State;
    public ActorRef<TEvent> Ref { get; }
    public IReadOnlyList<TEvent> ProcessedMessages => _core.Events;

    private ActorInstance(
        AutomatonRuntime<TAutomaton, TState, TEvent, TEffect> core,
        Channel<TEvent> mailbox,
        ActorRef<TEvent> actorRef)
    {
        _core = core;
        _mailbox = mailbox;
        Ref = actorRef;
    }

    public static ActorInstance<TAutomaton, TState, TEvent, TEffect> Spawn(
        string name,
        Func<TEffect, ActorRef<TEvent>, Task>? effectHandler = null)
    {
        var (state, _) = TAutomaton.Init();
        var mailbox = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var actorRef = new ActorRef<TEvent>(name, mailbox.Writer);

        // Observer: no-op — actor state is internal
        Observer<TState, TEvent, TEffect> observer = (_, _, _) => ValueTask.CompletedTask;

        // Interpreter: wraps the effect handler with self-reference
        Interpreter<TEffect, TEvent> interpreter = effectHandler is not null
            ? async effect =>
            {
                await effectHandler(effect, actorRef);
                return [];
            }
            : _ => new ValueTask<TEvent[]>([]);

        var core = new AutomatonRuntime<TAutomaton, TState, TEvent, TEffect>(
            state, observer, interpreter);

        var actor = new ActorInstance<TAutomaton, TState, TEvent, TEffect>(
            core, mailbox, actorRef);

        // Start the processing loop
        _ = actor.ProcessLoop();

        return actor;
    }

    public async Task Stop()
    {
        _mailbox.Writer.Complete();
        await _cts.CancelAsync();
    }

    public async Task DrainMailbox()
    {
        var sent = Volatile.Read(ref Ref._sentCount);
        while (Volatile.Read(ref _processedCount) < sent)
        {
            await Task.Yield();
        }
    }

    private async Task ProcessLoop()
    {
        try
        {
            await foreach (var message in _mailbox.Reader.ReadAllAsync(_cts.Token))
            {
                await _core.Dispatch(message);
                Interlocked.Increment(ref _processedCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
    }
}
```

Key design decisions:

1. **`Channel.CreateUnbounded`** — Unbounded mailbox. In production, consider bounded channels with backpressure.
2. **`SingleReader = true`** — Only the processing loop reads, enabling lock-free optimizations.
3. **`SingleWriter = false`** — Multiple actors (or external code) can send messages concurrently.
4. **Fire-and-forget processing loop** — `_ = actor.ProcessLoop()` runs in the background.
5. **Effect handler receives `ActorRef`** — The actor can send messages to itself via the ref.

## Step 3: Spawn and Send Messages

```csharp
var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>
    .Spawn("counter-1");

// Fire-and-forget: messages are queued
await actor.Ref.Tell(new CounterEvent.Increment());
await actor.Ref.Tell(new CounterEvent.Increment());
await actor.Ref.Tell(new CounterEvent.Increment());

// Wait for all messages to be processed
await actor.DrainMailbox();

Console.WriteLine(actor.State.Count); // 3
Console.WriteLine(actor.ProcessedMessages.Count); // 3

await actor.Stop();
```

## Step 4: Handle Effects

Pass an effect handler to `Spawn`. It receives the effect and a reference to the actor itself:

```csharp
var logs = new List<string>();

var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>
    .Spawn("counter-1", effectHandler: async (effect, self) =>
    {
        if (effect is CounterEffect.Log log)
        {
            logs.Add(log.Message);
        }
    });

await actor.Ref.Tell(new CounterEvent.Increment());
await actor.Ref.Tell(new CounterEvent.Increment());
await actor.Ref.Tell(new CounterEvent.Reset());

await actor.DrainMailbox();

Console.WriteLine(actor.State.Count);  // 0
Console.WriteLine(logs[0]);           // "Counter reset from 2"

await actor.Stop();
```

## Step 5: Concurrent Message Sending

Because messages are processed sequentially from the mailbox, the actor is inherently thread-safe — even when messages arrive from multiple threads:

```csharp
var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>
    .Spawn("concurrent-counter");

// Send 100 messages from many threads simultaneously
var tasks = Enumerable.Range(0, 100)
    .Select(_ => actor.Ref.Tell(new CounterEvent.Increment()).AsTask());
await Task.WhenAll(tasks);

await actor.DrainMailbox();

// Sequential processing guarantees correct final state
Console.WriteLine(actor.State.Count); // 100 — always exactly 100

await actor.Stop();
```

No locks, no race conditions. The channel serializes access automatically.

## Understanding DrainMailbox

`DrainMailbox` uses a counter-based approach instead of checking if the channel is empty:

```text
Tell() ──► Interlocked.Increment(sentCount)
                │
                ▼
         Channel.WriteAsync(message)
                │
                ▼
    ProcessLoop reads message
                │
                ▼
         core.Dispatch(message)
                │
                ▼
    Interlocked.Increment(processedCount)
```

**Why not just check `channel.Reader.Count == 0`?** Because there's a race: the message might be *read* from the channel but `Dispatch` is still in-flight. The counter approach waits until dispatch actually completes.

## The Full Picture

```text
┌─────────────────────────────────────────────┐
│              ActorInstance                    │
│                                             │
│  ActorRef.Tell(msg) ──► Channel (mailbox)   │
│                              │              │
│                              ▼              │
│                        ProcessLoop          │
│                              │              │
│                   Dispatch(message)          │
│                      │          │           │
│                      ▼          ▼           │
│              Transition   Observer (no-op)  │
│                                │            │
│                                ▼            │
│                        Interpreter(effect)  │
│                           │                 │
│                           ▼                 │
│                    effectHandler(effect, self)│
└─────────────────────────────────────────────┘
```

## Why This Works

| Actor concept | Implementation |
| ------------- | -------------- |
| Mailbox | `Channel<TEvent>` |
| Sequential processing | `await foreach` + `AutomatonRuntime` thread safety |
| State encapsulation | Private `_state` inside `AutomatonRuntime` |
| Behavior change | `Transition` returns new state |
| Side effects | Effect handler with self-reference |
| Supervision / restart | `Reset(state)` on the runtime |

The same `Counter` transition function drives the actor, the MVU runtime, and the event-sourced aggregate.

## What's Next

- **[Command Validation](05-command-validation.md)** — Validate commands before sending them to actors
- **[Observability](06-observability.md)** — Add distributed tracing to your actor system
