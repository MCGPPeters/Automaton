# AutomatonDiagnostics

`namespace Automaton`

Static class exposing OpenTelemetry-compatible tracing instrumentation.

## Definition

```csharp
public static class AutomatonDiagnostics
```

## Members

| Member | Type | Description |
| ------ | ---- | ----------- |
| `SourceName` | `string` | The activity source name: `"Automaton"`. Use this to subscribe to traces. |
| `Source` | `ActivitySource` | The `System.Diagnostics.ActivitySource` instance. **Internal** — used by the runtime to create spans. |

## Span Names

The runtime creates the following `Activity` spans:

| Span name | Created by | Description |
| --------- | ---------- | ----------- |
| `Automaton.Start` | `AutomatonRuntime.Start` | Covers runtime initialization (Init + first effect interpretation). |
| `Automaton.Dispatch` | `AutomatonRuntime.Dispatch` | Covers the full Transition → Observe → Interpret → Feedback cycle. |
| `Automaton.InterpretEffect` | `AutomatonRuntime.InterpretEffect` | Covers interpretation of one effect. |
| `Automaton.Decider.Start` | `DecidingRuntime.Start` | Covers Decider runtime initialization. |
| `Automaton.Decider.Handle` | `DecidingRuntime.Handle` | Covers command handling: Decide → Transition → Observe → Interpret. |

## Tags

Spans carry the following tags (attributes):

| Tag | Type | Added to | Description |
| --- | ---- | -------- | ----------- |
| `automaton.type` | `string` | Start, Dispatch, InterpretEffect, Decider.Start, Decider.Handle | The automaton/decider type name. |
| `automaton.state.type` | `string` | Start, Decider.Start | The state type name. |
| `automaton.event.type` | `string` | Dispatch | The event type name that triggered the transition. |
| `automaton.effect.type` | `string` | InterpretEffect | The effect type name being interpreted. |
| `automaton.command.type` | `string` | Decider.Handle | The command type name being handled. |
| `automaton.result` | `string` | Decider.Handle | `"ok"` or `"error"` — the outcome of command validation. |
| `automaton.error.type` | `string` | Decider.Handle | The error type name (only set when `result` is `"error"`). |

## Subscribing to Traces

Register the source name with your OpenTelemetry provider:

```csharp
using OpenTelemetry;
using OpenTelemetry.Trace;

var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(AutomatonDiagnostics.SourceName)  // "Automaton"
    .AddConsoleExporter()
    .Build();
```

Then every `Dispatch` and `Handle` call emits spans automatically — no code changes needed.

## Testing Traces

Collect traces in-process using `ActivityListener`:

```csharp
var activities = new List<Activity>();

using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == AutomatonDiagnostics.SourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity => activities.Add(activity)
};
ActivitySource.AddActivityListener(listener);

// Run your runtime...
await runtime.Dispatch(someEvent);

Assert.True(activities.Exists(a => a.OperationName == "Automaton.Dispatch"));
```

## See Also

- [Observability Tutorial](../tutorials/06-observability.md) — end-to-end tracing walkthrough
- [Runtime](runtime.md) — where spans are created
- [Decider](decider.md) — `DecidingRuntime.Handle` spans
