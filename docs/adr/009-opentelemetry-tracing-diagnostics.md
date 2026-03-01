# ADR-009: OpenTelemetry-Compatible Tracing via System.Diagnostics

**Status:** Accepted
**Date:** 2025-06-14
**Deciders:** Maurice Peters

## Context

The hardened runtime (ADR-008) is functionally complete but opaque at runtime — there is no way for operators to observe what the automaton is doing in production. Questions like:

- How long does a `Dispatch` take?
- How often are commands rejected by the Decider?
- Is the feedback loop depth increasing over time?
- Which automaton types are busiest?

…require distributed tracing instrumentation.

### Constraints

1. **Zero external dependencies** — the Automaton package has no dependencies. Adding an OpenTelemetry SDK reference would pull in a large transitive dependency graph.
2. **Opt-in overhead** — instrumentation must have near-zero cost when no telemetry collector is listening.
3. **Standard protocol** — the instrumentation must work with any OpenTelemetry-compatible collector (Jaeger, Zipkin, OTLP, Application Insights, etc.).

## Decision

Instrument the runtime using `System.Diagnostics.ActivitySource` and `System.Diagnostics.Activity` — the .NET BCL's built-in tracing primitives that OpenTelemetry natively understands.

### The Diagnostics Class

A single static class exposes the `ActivitySource`:

```csharp
public static class AutomatonDiagnostics
{
    public const string SourceName = "Automaton";

    internal static ActivitySource Source { get; } = new(
        SourceName,
        typeof(AutomatonDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
```

- `SourceName` is `public const` so application developers can register it with their pipeline.
- `Source` is `internal` — only the runtime creates spans; consumers cannot create spans with the library's source.
- The version is read from the assembly at startup (via Nerdbank.GitVersioning).

### Span Coverage

Five span types cover all runtime entry points:

| Span Name | Location | Tags |
|-----------|----------|------|
| `Automaton.Start` | `AutomatonRuntime.Start()` | `automaton.type`, `automaton.state.type` |
| `Automaton.Dispatch` | `AutomatonRuntime.Dispatch()` | `automaton.type`, `automaton.event.type` |
| `Automaton.InterpretEffect` | `AutomatonRuntime.InterpretEffect()` | `automaton.type`, `automaton.effect.type` |
| `Automaton.Decider.Start` | `DecidingRuntime.Start()` | `automaton.type`, `automaton.state.type` |
| `Automaton.Decider.Handle` | `DecidingRuntime.Handle()` | `automaton.type`, `automaton.command.type`, `automaton.result`, `automaton.error.type` |

### Tag Conventions

All tags use the `automaton.` prefix to avoid collisions with other instrumentation:

| Tag | Value | Example |
|-----|-------|---------|
| `automaton.type` | Automaton type name | `"BoundedCounter"` |
| `automaton.state.type` | State type name | `"CounterState"` |
| `automaton.event.type` | Event type name | `"Increment"` |
| `automaton.effect.type` | Effect type name | `"None"` |
| `automaton.command.type` | Command type name | `"Add"` |
| `automaton.result` | `"ok"` or `"error"` | `"error"` |
| `automaton.error.type` | Error type name (on rejection) | `"Overflow"` |

### Status Code Semantics

- **Success**: `ActivityStatusCode.Ok` on all successful completions.
- **Infrastructure error**: `ActivityStatusCode.Error` with `ex.Message` on exceptions (observer/interpreter failures).
- **Command rejection**: `ActivityStatusCode.Ok` — a rejected command is a *correct* business outcome, not a fault. The `automaton.result` tag distinguishes ok from error.

This distinction is critical: alerting on `ActivityStatusCode.Error` catches infrastructure problems without false positives from valid business rejections.

### Opt-In Pattern

When no `ActivityListener` is registered, `ActivitySource.StartActivity()` returns `null`. The `using var activity = ...` pattern handles this gracefully — `null?.SetTag(...)` is a no-op, and `using null` disposes nothing. The overhead is a single null check per span — effectively zero.

Application developers opt in by registering the source name:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(AutomatonDiagnostics.SourceName));
```

### Instrumentation Pattern

Every instrumented method follows the same pattern:

```csharp
public async Task Dispatch(TEvent @event, CancellationToken cancellationToken = default)
{
    using var activity = AutomatonDiagnostics.Source.StartActivity("Automaton.Dispatch");
    activity?.SetTag("automaton.type", typeof(TAutomaton).Name);
    activity?.SetTag("automaton.event.type", @event?.GetType().Name);

    await _gate.WaitAsync(cancellationToken);
    try
    {
        await DispatchCore(@event, cancellationToken);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        throw;
    }
    finally
    {
        _gate.Release();
    }
}
```

The activity is created *before* acquiring the lock, so the span duration includes wait time. This is intentional — high lock contention shows up as long span durations, making it visible in traces.

## Mathematical Grounding

### Tracing as a Natural Transformation

Tracing is a **natural transformation** between the "untraced" runtime functor and the "traced" runtime functor:

$$\eta : \text{Runtime} \Rightarrow \text{Runtime}_{\text{traced}}$$

The naturality condition ensures that tracing does not alter the semantics:

$$\forall\;e : \text{dispatch}_{\text{traced}}(e) \equiv \text{dispatch}(e)$$

The traced runtime produces identical state transitions and effects — it merely emits additional observations (spans) as a side channel.

### Spans as a Writer Monad

The span emissions form a **Writer monad** alongside the runtime's existing state monad:

$$\text{Traced}\;A = \text{Writer}\langle [\text{Span}],\; A \rangle$$

Each runtime operation produces both its normal result and a list of spans. The monoidal structure of spans (concatenation) allows composing traced operations:

$$\text{span}_1 \mathbin{+\!\!\!+} \text{span}_2 \mathbin{+\!\!\!+} \ldots$$

### Observability Without Coupling

The `ActivitySource`/`ActivityListener` pattern implements the **Observer design pattern** at the infrastructure level — decoupling the instrumentation (source) from the collection (listener). This is analogous to the runtime's own Observer/Interpreter pattern:

| Level | Producer | Consumer | Coupling |
|-------|----------|----------|----------|
| Domain | Transition function | Observer | Delegate injection |
| Infrastructure | ActivitySource | ActivityListener | Name-based registration |

Both levels achieve the same goal: separation of what happens (transition/span) from who cares (renderer/collector).

## Consequences

### Positive

- **Zero dependencies** — uses only `System.Diagnostics` from the BCL. No OpenTelemetry SDK reference needed.
- **Near-zero overhead** — when no listener is active, cost is one null check per span.
- **Universal compatibility** — works with any OpenTelemetry-compatible collector.
- **Business-aware status codes** — command rejection is `Ok`, not `Error`, preventing false alerts.
- **Lock contention visibility** — span duration includes semaphore wait time.

### Negative

- **No metrics** — this ADR covers tracing only. Metrics (counters, histograms) are deferred to a future ADR.
- **No span links** — feedback events do not link back to the parent dispatch span. Adding span links would require threading `ActivityContext` through the feedback loop.
- **Type names only** — tags use `GetType().Name`, not full qualified names. This is sufficient for most scenarios but may be ambiguous if multiple namespaces define types with the same name.

### Neutral

- The `SourceName` is the string `"Automaton"`. If multiple versions of the library are loaded (rare), they share the same source name. This is acceptable — the version tag on the `ActivitySource` disambiguates.
- Internal `Core` methods (e.g., `DispatchCore`) are not independently instrumented. They execute within the parent span, keeping the span tree shallow and reducing overhead.

## References

- OpenTelemetry Specification. "Tracing API." [opentelemetry.io](https://opentelemetry.io/docs/specs/otel/trace/api/).
- .NET Documentation. "Distributed tracing instrumentation." [learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs).
- .NET Documentation. "ActivitySource Class." [learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.activitysource).
