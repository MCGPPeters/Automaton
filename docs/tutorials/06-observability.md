# Tutorial 06: Observability with OpenTelemetry

Add distributed tracing to your automaton with zero external dependencies.

> **API reference:** For the complete span and tag inventory, see [Diagnostics Reference](../reference/diagnostics.md).

## What You'll Learn

- How the Automaton emits tracing spans via `System.Diagnostics.ActivitySource`
- How to enable collection with OpenTelemetry
- What spans and tags are emitted
- How to listen for spans in tests
- How command rejections are represented in traces

## Prerequisites

Complete [Tutorial 01: Getting Started](01-getting-started.md). Familiarity with the [DecidingRuntime](05-command-validation.md) is helpful for the Decider tracing section.

## How It Works

The Automaton library instruments itself using `System.Diagnostics.ActivitySource` — the same API that ASP.NET Core, HttpClient, and other .NET libraries use. This means:

- **Zero external dependencies** — no OpenTelemetry SDK package needed in the library
- **Near-zero overhead** when no listener is registered (`StartActivity()` returns `null`)
- **Compatible with any collector** — Jaeger, Zipkin, OTLP, Application Insights, etc.

```csharp
public static class AutomatonDiagnostics
{
    public const string SourceName = "Automaton";

    internal static ActivitySource Source { get; } = new(
        SourceName,
        typeof(AutomatonDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
```

## Step 1: Enable Tracing in Your Application

Register the Automaton source with your OpenTelemetry pipeline:

```csharp
using Automaton;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(AutomatonDiagnostics.SourceName)  // ← this line
        .AddConsoleExporter()
        .AddOtlpExporter());
```

That's it. All Automaton runtime operations will now emit spans.

## Step 2: Understand the Spans

### AutomatonRuntime Spans

| Span Name | When | Tags |
| --------- | ---- | ---- |
| `Automaton.Start` | Runtime initialization | `automaton.type`, `automaton.state.type` |
| `Automaton.Dispatch` | Each event dispatch | `automaton.type`, `automaton.event.type` |
| `Automaton.InterpretEffect` | Each effect interpretation | `automaton.type`, `automaton.effect.type` |

### DecidingRuntime Spans

| Span Name | When | Tags |
| --------- | ---- | ---- |
| `Automaton.Decider.Start` | Decider runtime initialization | `automaton.type`, `automaton.state.type` |
| `Automaton.Decider.Handle` | Each command handled | `automaton.type`, `automaton.command.type`, `automaton.result`, `automaton.error.type` |

### Tag Semantics

| Tag | Values | Purpose |
| --- | ------ | ------- |
| `automaton.type` | e.g., `Counter` | Which automaton produced this span |
| `automaton.event.type` | e.g., `Increment` | The event type dispatched |
| `automaton.effect.type` | e.g., `None`, `Log` | The effect type produced |
| `automaton.command.type` | e.g., `Add`, `Reset` | The command type handled |
| `automaton.result` | `ok` or `error` | Whether the command was accepted or rejected |
| `automaton.error.type` | e.g., `Overflow` | The error type (only on rejection) |

## Step 3: Reading Traces

### Successful Command

```text
Automaton.Decider.Handle
  automaton.type = Counter
  automaton.command.type = Add
  automaton.result = ok
  status = Ok
  └── Automaton.Dispatch (×5)
        automaton.type = Counter
        automaton.event.type = Increment
        status = Ok
```

### Rejected Command

```text
Automaton.Decider.Handle
  automaton.type = Counter
  automaton.command.type = Add
  automaton.result = error
  automaton.error.type = Overflow
  status = Ok          ← not Error! Rejection is correct behavior.
```

> **Important:** Command rejections use `ActivityStatusCode.Ok`, not `Error`. A rejected command is a correct business outcome — the system worked as intended. `Error` status is reserved for infrastructure faults (exceptions, timeouts).

## Step 4: Listen for Spans in Tests

You can capture spans in tests using `ActivityListener`:

```csharp
using System.Diagnostics;
using Automaton;

public class TracingTests
{
    [Fact]
    public async Task Dispatch_EmitsSpan()
    {
        var activities = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AutomatonDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect, Unit>
            .Start(
                default,
                observer: (_, _, _) => PipelineResult.Ok,
                interpreter: _ => new ValueTask<Result<CounterEvent[], PipelineError>>(
                    Result<CounterEvent[], PipelineError>.Ok([])));

        await runtime.Dispatch(new CounterEvent.Increment());

        var dispatchSpan = activities.First(a =>
            a.OperationName == "Automaton.Dispatch");

        Assert.Equal("Counter",
            dispatchSpan.GetTagItem("automaton.type"));
        Assert.Equal("Increment",
            dispatchSpan.GetTagItem("automaton.event.type"));
        Assert.Equal(ActivityStatusCode.Ok, dispatchSpan.Status);
    }
}
```

### Testing Decider Traces

```csharp
[Fact]
public async Task Handle_Rejection_EmitsErrorTypeTag()
{
    var activities = new List<Activity>();

    using var listener = new ActivityListener
    {
        ShouldListenTo = source => source.Name == AutomatonDiagnostics.SourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = activity => activities.Add(activity)
    };
    ActivitySource.AddActivityListener(listener);

    var runtime = await DecidingRuntime<Counter, CounterState, CounterCommand,
        CounterEvent, CounterEffect, CounterError, Unit>.Start(
            default,
            (_, _, _) => PipelineResult.Ok,
            _ => new ValueTask<Result<CounterEvent[], PipelineError>>(
                Result<CounterEvent[], PipelineError>.Ok([])));

    await runtime.Handle(new CounterCommand.Add(200));

    var handleSpan = activities.First(a =>
        a.OperationName == "Automaton.Decider.Handle");

    Assert.Equal("error",
        handleSpan.GetTagItem("automaton.result"));
    Assert.Equal("Overflow",
        handleSpan.GetTagItem("automaton.error.type"));
    Assert.Equal(ActivityStatusCode.Ok, handleSpan.Status); // not Error!
}
```

## Step 5: Integration with Collectors

### Console Exporter (Development)

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(AutomatonDiagnostics.SourceName)
        .AddConsoleExporter());
```

### OTLP (Jaeger, Grafana Tempo, etc.)

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(AutomatonDiagnostics.SourceName)
        .AddOtlpExporter(opts =>
        {
            opts.Endpoint = new Uri("http://localhost:4317");
        }));
```

### Azure Monitor / Application Insights

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(AutomatonDiagnostics.SourceName))
    .UseAzureMonitor(opts =>
    {
        opts.ConnectionString = "InstrumentationKey=...";
    });
```

## Zero-Overhead When Disabled

When no `ActivityListener` is registered for the `"Automaton"` source, `ActivitySource.StartActivity()` returns `null`. The `?.SetTag()` calls are no-ops. This is the standard .NET pattern for zero-overhead instrumentation:

```csharp
// From Runtime.cs — near-zero cost when no listener is active
using var activity = AutomatonDiagnostics.Source.StartActivity("Automaton.Dispatch");
activity?.SetTag("automaton.type", typeof(TAutomaton).Name);     // null-conditional: no-op
activity?.SetTag("automaton.event.type", @event?.GetType().Name); // null-conditional: no-op
```

## What You've Built

You now have full observability into your automaton runtimes:

- **Every dispatch** produces a span with the event type
- **Every command** produces a span with acceptance/rejection status
- **Every effect** produces a span with the effect type
- **Error context** is captured (error type, not just "failed")
- **Zero overhead** when tracing is disabled
- **Zero dependencies** in the library itself

This works identically across all runtimes — MVU, Event Sourcing, and Actors all inherit tracing from the shared `AutomatonRuntime`.

### Deepen Your Understanding

| Topic | Link |
| ----- | ---- |
| Full span and tag inventory | [Diagnostics Reference](../reference/diagnostics.md) |
| Runtime internals that emit spans | [Runtime Reference](../reference/runtime.md) |
| DecidingRuntime Handle spans | [Decider Reference](../reference/decider.md) |
| Testing strategies including tracing | [Testing Strategies](../guides/testing-strategies.md) |
| Choosing a runtime | [Runtimes Compared](../concepts/runtimes-compared.md) |
