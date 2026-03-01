# ADR-008: Production Hardening — Thread Safety, Cancellation, and Depth Guards

**Status:** Accepted
**Date:** 2025-06-14
**Deciders:** Maurice Peters

## Context

The `AutomatonRuntime` (ADR-002) defines the structural core: a monadic left fold with Observer and Interpreter. The original implementation was functionally correct but lacked production guarantees:

1. **No thread safety** — concurrent `Dispatch` calls could interleave, causing data races on `_state` and `_events`.
2. **No cancellation** — async methods did not accept `CancellationToken`, making them impossible to cancel gracefully.
3. **Unbounded feedback** — the interpreter feedback loop (`effect → events → effect → …`) could recurse infinitely, causing stack overflows.
4. **No null validation** — invalid observer/interpreter delegates were accepted silently, causing `NullReferenceException` at dispatch time.

These are not design flaws — the runtime's mathematical structure (ADR-002) is sound. They are implementation gaps between a correct prototype and a production-hardened library.

## Decision

Harden the `AutomatonRuntime` and `DecidingRuntime` with four production guarantees, preserving the existing mathematical semantics.

### 1. Thread Safety via SemaphoreSlim

All public mutating methods (`Dispatch`, `InterpretEffect`) are serialized via a `SemaphoreSlim(1, 1)`:

```csharp
private readonly SemaphoreSlim _gate = new(1, 1);

public async Task Dispatch(TEvent @event, CancellationToken cancellationToken = default)
{
    await _gate.WaitAsync(cancellationToken);
    try
    {
        await DispatchCore(@event, cancellationToken);
    }
    finally
    {
        _gate.Release();
    }
}
```

**Why SemaphoreSlim, not `lock`?** The runtime is async (`Task`-returning). C#'s `lock` statement cannot be used with `await` — it would block the thread, defeating the purpose of async. `SemaphoreSlim` is the idiomatic async mutual exclusion primitive.

**Why not `Channel<TEvent>` (mailbox)?** A channel would change the API semantics from "dispatch completes when the transition is done" to "dispatch completes when the event is enqueued." The current semantics are simpler and allow callers to observe state immediately after `await Dispatch(...)`. The Actor runtime (ADR-007) already uses a channel for its mailbox — the base runtime stays synchronous-in-dispatch.

### Public/Private Split for Re-Entrancy

The feedback loop creates a re-entrancy problem: `Dispatch` → `InterpretEffectCore` → `DispatchCore` → `InterpretEffectCore` → … If both `Dispatch` and `InterpretEffect` acquired the semaphore, the recursive call would deadlock.

Solution: **public methods acquire the lock; private `Core` methods do the work unlocked**:

```
Dispatch (acquires gate)
  └─ DispatchCore (no lock)
       └─ InterpretEffectCore (no lock)
            └─ DispatchCore (no lock) ← feedback, no deadlock
```

This pattern ensures:
- External callers are serialized (thread safety)
- Internal recursion flows freely (no deadlock)
- The lock is held for the entire dispatch-observe-interpret chain (atomicity)

### 2. CancellationToken Support

All async methods accept an optional `CancellationToken`:

```csharp
public async Task Dispatch(TEvent @event, CancellationToken cancellationToken = default)
public static async Task<...> Start(..., CancellationToken cancellationToken = default)
public async Task InterpretEffect(TEffect effect, CancellationToken cancellationToken = default)
public async Task<Result<TState, TError>> Handle(TCommand command, CancellationToken cancellationToken = default)
```

Cancellation is checked:
- Before acquiring the semaphore (`_gate.WaitAsync(cancellationToken)`)
- At the start of each `DispatchCore` and `InterpretEffectCore` call (`cancellationToken.ThrowIfCancellationRequested()`)

The parameter defaults to `default` (no cancellation), preserving backward compatibility.

### 3. Feedback Depth Guard

The interpreter feedback loop is bounded by a constant:

```csharp
public const int MaxFeedbackDepth = 64;

private async Task InterpretEffectCore(TEffect effect, CancellationToken cancellationToken, int depth = 0)
{
    if (depth > MaxFeedbackDepth)
        throw new InvalidOperationException(
            $"Interpreter feedback loop exceeded maximum depth of {MaxFeedbackDepth}. " +
            "This usually indicates an infinite feedback cycle where an effect always " +
            "produces events whose transitions produce the same effect.");
    // ...
}
```

**Why 64?** This is a pragmatic choice. Legitimate feedback chains (e.g., saga orchestration with multiple steps) rarely exceed single-digit depth. 64 provides ample headroom while catching runaway loops early enough to produce a useful error message.

The depth counter is threaded through `DispatchCore` → `InterpretEffectCore` → `DispatchCore` as a parameter, not stored as mutable state.

### 4. Null Safety (ArgumentNullException)

The constructor validates observer and interpreter at construction time:

```csharp
public AutomatonRuntime(TState initialState, Observer<...> observer, Interpreter<...> interpreter)
{
    _state = initialState;
    _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
}
```

Fail-fast at construction is preferable to `NullReferenceException` at dispatch time — it gives a clear error message at the point where the mistake was made.

## Mathematical Grounding

### Serialization as Linearizability

The semaphore ensures **linearizability** — every dispatch appears to take effect instantaneously at some point between its invocation and return. Concurrent dispatches form a total order:

$$d_1 < d_2 < d_3 < \ldots$$

This preserves the sequential semantics of the monadic left fold (ADR-002):

$$s_n = T(s_{n-1}, e_n)$$

Without serialization, concurrent dispatches could observe stale state, producing incorrect transitions. The semaphore makes the runtime behave as if all dispatches arrive on a single thread.

### Cancellation as Cooperative Interruption

Cancellation is **cooperative**, not preemptive. The runtime checks for cancellation at defined points (before lock acquisition, before each dispatch/interpret step) and throws `OperationCanceledException` if requested. Between checks, the transition runs to completion — partial transitions are not possible.

This preserves the **atomicity of individual transitions**: a transition either completes fully (state advances) or is never started (state unchanged). Cancellation can only occur *between* transitions.

### Depth Guard as Well-Foundedness

The feedback depth guard ensures the interpreter feedback loop is **well-founded** — it has a finite descent:

$$\text{depth} : \mathbb{N} \to \text{depth} \leq 64$$

This is a necessary condition for the monadic left fold to terminate. Without it, a pathological interpreter (one that always produces events whose effects produce more events) would loop forever.

The bound makes the runtime's feedback loop a **bounded recursion**, converting a potentially non-terminating computation into a total one (it either completes or throws).

## Consequences

### Positive

- **Thread safety by default** — consumers do not need to add their own synchronization.
- **Graceful shutdown** — cancellation tokens enable clean teardown in hosted services and long-running processes.
- **Debuggable failures** — depth guard and null checks produce clear, actionable error messages.
- **Backward compatible** — all changes are additive. Existing code that does not use cancellation or concurrent dispatch continues to work unchanged.

### Negative

- **Serialization overhead** — the semaphore adds a small overhead per dispatch (~1μs on modern hardware). For extremely high-throughput scenarios, this may be significant. (Mitigated by the Actor runtime's channel-based approach for concurrent workloads.)
- **No parallel dispatch** — concurrent events are queued, not parallelized. This is by design (state machine semantics require sequential transitions) but limits throughput to one event at a time per runtime instance.

### Neutral

- The `MaxFeedbackDepth` constant is not configurable at runtime. If a specific use case needs deeper feedback chains, the constant can be changed at the source level. Making it configurable would add API surface for an edge case.
- The `State` and `Events` properties remain lock-free reads. They may observe intermediate values during an in-flight dispatch. This is acceptable because the properties are informational, not transactional.

## References

- Herlihy, M. & Wing, J. (1990). "Linearizability: A Correctness Condition for Concurrent Objects." *ACM TOPLAS*, 12(3), 463–492.
- Stephen Toub. (2012). "Building Async Coordination Primitives." *MSDN Blog*.
- .NET Documentation. "SemaphoreSlim Class." [learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim).
