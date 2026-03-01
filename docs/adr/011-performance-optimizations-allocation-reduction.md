# ADR 011 — Performance Optimizations: Allocation Reduction

**Status:** Accepted
**Date:** 2025-02-28

## Context

After establishing the core runtime (ADR 002), production hardening (ADR 008), and tracing (ADR 009), profiling revealed several avoidable allocations on the hot path — `Dispatch` and `Handle`.  The library targets high-throughput event-sourced and actor systems where per-operation GC pressure directly impacts tail latency.

Six targeted optimizations were identified and applied incrementally, each verified by BenchmarkDotNet (`MemoryDiagnoser`).

## Decision

### Step A — Async Elision (IsCompletedSuccessfully fast paths)

Every `async ValueTask` method in the library was converted to a **sync-fast / async-slow** pattern:

```csharp
// Before — always allocates state machine
public async ValueTask Dispatch(TEvent @event) { ... }

// After — elides state machine when everything completes synchronously
public ValueTask Dispatch(TEvent @event)
{
    // ... synchronous work ...
    var interpreterTask = _interpreter(@event, state, effect);
    return interpreterTask.IsCompletedSuccessfully
        ? DispatchFeedbackEvents(interpreterTask.Result)        // sync tail
        : AwaitInterpreterThenDispatch(interpreterTask);        // async slow path
}
```

Applied to all methods in `Runtime.cs` (Dispatch, DispatchFeedbackEvents, InterpretEffect) and `Decider.cs` (Handle, DispatchEventsAndReturnOk, AwaitGateThenHandle).

**Allocation impact:** Zero change in `MemoryDiagnoser` output (benchmarks already completed synchronously via `ValueTask`), but eliminates state-machine allocation on production paths where `SemaphoreSlim.WaitAsync` and interpreter delegates complete synchronously — the common case.

### Step B — Struct Result (`readonly struct`)

`Result<T, E>` was converted from a class hierarchy to a `readonly struct` with a boolean discriminator:

```csharp
public readonly struct Result<T, E>
{
    public bool IsOk { get; }
    public T Value { get; }
    public E Error { get; }
    public static Result<T, E> Ok(T value) => new(value);
    public static Result<T, E> Err(E error) => new(error);
}
```

**Allocation impact:** −48 B on every `Handle` path (eliminates heap allocation for the `Result` wrapper).

### Step C — `IEnumerable<TEvent>` → `TEvent[]`

The `Interpreter` delegate return type and `Decider.Decide` return type were narrowed from `IEnumerable<TEvent>` to `TEvent[]`:

```csharp
// Before
delegate ValueTask<IEnumerable<TEvent>> Interpreter<...>(...);
Result<IEnumerable<TEvent>, TError> Decide(TCommand command, TState state);

// After
delegate ValueTask<TEvent[]> Interpreter<...>(...);
Result<TEvent[], TError> Decide(TCommand command, TState state);
```

This also enabled:
- **Eliminating the `EqualityComparer<TEvent>` skip pattern** in `AwaitRemainingEventsAndReturnOk` — replaced with simple `int startIndex` and array indexing.
- **Eliminating `List<TEvent> materializedEvents`** in `AggregateRunner.Handle` — the array is passed directly to `EventStore.Append`.
- **Index-based for-loops** instead of `foreach` enumeration in all event dispatch paths.

**Allocation impact:** −24 B on interpreter-feedback and Handle-accept paths (eliminates enumerator allocation).

### Step D — `PoolingAsyncValueTaskMethodBuilder`

All 12 async methods across `Runtime.cs` and `Decider.cs` were annotated with `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]` (or the generic `<>` variant for `ValueTask<T>` returns):

```csharp
[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
private async ValueTask AwaitCoreThenRelease(ValueTask coreTask)
{
    try { await coreTask.ConfigureAwait(false); }
    finally { _gate.Release(); }
}
```

When an async method hits the **slow path** (actual suspension at `await`), the runtime must allocate a state-machine object on the heap. The pooling builder **rents** these state machines from `ObjectPool` instead of allocating fresh ones each time, amortizing GC pressure across many calls.

**Allocation impact:** Zero change in `MemoryDiagnoser` (benchmarks complete synchronously — the fast path never allocates a state machine regardless). The value manifests under real-world I/O where `SemaphoreSlim.WaitAsync` or interpreter delegates genuinely suspend.

### Step E — Optional Thread Safety (`threadSafe` parameter)

A `bool threadSafe = true` parameter was added to `AutomatonRuntime.Start` and `DecidingRuntime.Start`. When `false`, the runtime skips `SemaphoreSlim` entirely — no `WaitAsync`, no `Release`, no gate allocation:

```csharp
public ValueTask Dispatch(TEvent @event)
{
    if (_threadSafe)
    {
        var gateTask = _gate.WaitAsync();
        // ... existing serialized path ...
    }
    // unserialized fast path — no SemaphoreSlim
    return DispatchUnserialized(@event);
}
```

Dedicated `DispatchUnserialized`, `InterpretEffectUnserialized`, `HandleUnserialized` code paths were added to avoid the gate overhead entirely. Suitable for single-threaded scenarios: actors, UI loops, test harnesses.

**Allocation impact:** Zero when uncontended — `SemaphoreSlim.WaitAsync()` returns a cached `Task` when the semaphore is available. The benefit is **latency** (fewer branches, no lock acquisition), not allocation. Under contention, this avoids `TaskNode` allocations that `SemaphoreSlim` creates to queue waiters.

### Step F — Conditional Event History Tracking (`trackEvents` parameter)

A `bool trackEvents = true` parameter was added alongside `threadSafe`. When `false`, the internal `List<TEvent> _events` is never allocated — `_events` is `null`, and all `_events?.Add(@event)` calls become no-ops:

```csharp
// Constructor
_events = trackEvents ? [] : null;

// DispatchCore — null-conditional avoids allocation
_events?.Add(@event);

// Events property — returns empty array when tracking disabled
public IReadOnlyList<TEvent> Events => _events ?? (IReadOnlyList<TEvent>)Array.Empty<TEvent>();
```

This was substituted for the originally planned `ReadOnlyMemory<TEvent>` optimization (ADR 011 Future Considerations), which analysis showed provides **zero benefit**: collection expressions (`[e1, e2]`) cannot target `ReadOnlyMemory<T>` (no `CollectionBuilderAttribute`), `Span<T>` cannot cross `await` boundaries, and wrapping an existing `TEvent[]` in `ReadOnlyMemory` adds indirection without eliminating any allocation.

**Allocation impact:** −56 B per dispatch and handle-accept operation. The first `List<TEvent>.Add()` on a fresh list grows the internal backing array from capacity 0 to 4, allocating a 56-byte `TEvent[]` (16 B object header + 8 B length + 4 × 8 B reference slots on ARM64). When `trackEvents: false`, this allocation never occurs. This savings applies **regardless** of the `threadSafe` setting — thread-safe runtimes benefit equally.

## Benchmark Results

BenchmarkDotNet v0.15.8 · .NET 10.0.2 · Apple M4 Pro

### Progression (Steps A–C)

| Benchmark | Baseline | + Struct Result | + TEvent[] | Total Δ |
|---|---:|---:|---:|---|
| Dispatch (no-op) | 128 B | 128 B | 128 B | — |
| Dispatch (observer) | 128 B | 128 B | 128 B | — |
| Dispatch × 100 | 9,360 B | 9,360 B | 9,360 B | — |
| Dispatch (feedback) | 256 B | 256 B | **232 B** | **−24 B (−9%)** |
| Dispatch (composed) | 128 B | 128 B | 128 B | — |
| Handle accept | 256 B | 208 B | **184 B** | **−72 B (−28%)** |
| Handle reject | 96 B | 48 B | 48 B | **−48 B (−50%)** |

### Allocation by Mode (Steps D–F)

| Benchmark | Default | Safe, no track | Lean | Δ (no track) |
|---|---:|---:|---:|---|
| Dispatch (no-op) | 128 B | **72 B** | **72 B** | **−56 B (−44%)** |
| Dispatch (feedback) | 232 B | **176 B** | **176 B** | **−56 B (−24%)** |
| Handle accept | 184 B | **128 B** | **128 B** | **−56 B (−30%)** |
| Handle reject | 48 B | 48 B | 48 B | — |

> **Key finding:** `SemaphoreSlim` adds **zero bytes** when uncontended (returns a cached `Task`).
> The entire −56 B savings come from `trackEvents: false` alone.
> Thread-safe runtimes achieve identical allocation to lean mode by setting just `trackEvents: false`.

### Final Allocation Breakdown (Default Mode)

| Path | Allocated | Source |
|---|---:|---|
| Dispatch (no-op / observer / composed) | 128 B | SemaphoreSlim coordination + ValueTask plumbing |
| Dispatch (feedback, 1 level) | 232 B | 128 B base + 104 B for second Dispatch cycle |
| Dispatch × 100 | 9,360 B | 93.6 B amortized per dispatch (includes array allocations) |
| Handle accept (1 event) | 184 B | 128 B base + 56 B for Decider coordination |
| Handle reject (0 events) | 48 B | Short-circuit — no Dispatch, no array, struct Result on stack |

### Final Allocation Breakdown (trackEvents=false, any threadSafe setting)

| Path | Allocated | Source |
|---|---:|---|
| Dispatch (no-op) | 72 B | ValueTask plumbing only — no gate alloc, no history list |
| Dispatch (feedback, 1 level) | 176 B | 72 B base + 104 B for second Dispatch cycle |
| Handle accept (1 event) | 128 B | 72 B base + 56 B for Decider coordination |
| Handle reject (0 events) | 48 B | Short-circuit — identical to default mode |

## Consequences

### Positive

- **28% allocation reduction on Handle (accept)** — the most critical path in event-sourced systems.
- **50% allocation reduction on Handle (reject)** — common validation failure path.
- **Array indexing** replaces `IEnumerable` enumeration — eliminates enumerator allocation and enables bounds-elided loops.
- **Simpler continuation logic** — `startIndex` integer replaces brittle `EqualityComparer` skip pattern (which failed on duplicate events).
- **No API break for consumers using collection expressions** — `[event1, event2]` already produces `T[]` in C# 12+.
- **`trackEvents: false` (−44% dispatch, −30% handle-accept)** — eliminates 56 B per operation from `List<TEvent>` backing array growth. Works with both `threadSafe: true` and `threadSafe: false` — `SemaphoreSlim` adds zero allocation when uncontended.
- **`threadSafe: false`** — eliminates semaphore branch overhead for single-threaded scenarios (actors, UI loops). Purely a latency win, not an allocation win.
- **Pooling async state machines** — `PoolingAsyncValueTaskMethodBuilder` amortizes GC pressure on the async slow path (real-world I/O with actual suspension).

### Negative

None. The library has not been released — there are no external consumers, so these changes carry zero API compatibility cost.

### Future Considerations

- **`ReadOnlyMemory<TEvent>`** — evaluated and **rejected**: collection expressions cannot target `ReadOnlyMemory<T>` (no `CollectionBuilderAttribute`), `Span<T>` cannot cross `await` boundaries, and wrapping `TEvent[]` adds indirection without eliminating allocation. Conditional event tracking (Step F) was substituted as a more impactful optimization.
- **Custom `ObjectPool<TStateMachine>`** — if profiling shows `PoolingAsyncValueTaskMethodBuilder`'s default pool is a bottleneck, a dedicated pool with tuned capacity could further reduce contention.
- **`IThreadPoolWorkItem`** — manual scheduling could bypass `Task` allocation for fire-and-forget observer notifications.

## Mathematical Grounding

The array narrowing preserves the **free-monoid** structure of event sequences (`TEvent[]` is isomorphic to `List<TEvent>` as a free monoid under concatenation), while providing O(1) length and O(1) random access — properties the monadic left fold exploits for index-based iteration.

The struct `Result` optimization replaces a heap-allocated sum type with a stack-allocated one, preserving the categorical structure (`Result<T, E> ≅ T + E`) while eliminating the indirection penalty.

The `trackEvents: false` mode removes the event accumulator (`List<TEvent>`), leaving only the pure fold kernel — the minimal Mealy machine transition `(state, event) → (state, effect)` plus the ValueTask plumbing required by the .NET async model. The `threadSafe: false` mode additionally removes the serialization gate (`SemaphoreSlim`), eliminating branch overhead but adding no further allocation savings when uncontended.
