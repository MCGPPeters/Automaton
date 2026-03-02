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
| `Automaton.Dispatch` | `AutomatonRuntime.Dispatch` | Covers the full Transition → Observe → Interpret → Feedback cycle. |
| `Automaton.Transition` | `AutomatonRuntime.Dispatch` | Covers a single `Transition(state, event)` call. |
| `Automaton.InterpretEffect` | `AutomatonRuntime.InterpretEffect` | Covers interpretation of one effect. |
| `Automaton.Handle` | `DecidingRuntime.Handle` | Covers command handling: Decide → Transition → Observe → Interpret. |
| `Automaton.Decide` | `DecidingRuntime.Handle` | Covers a single `Decide(state, command)` call. |

## Tags

Spans carry the following tags (attributes):

| Tag | Type | Added to | Description |
| --- | ---- | -------- | ----------- |
| `automaton.event_count` | `int` | Dispatch, Handle | Number of events produced. |
| `automaton.effect_count` | `int` | Dispatch, Handle | Number of effects produced. |
| `automaton.feedback_depth` | `int` | Dispatch | Current recursion depth in the feedback loop. |
| `automaton.is_terminal` | `bool` | Handle | Whether the Decider reached a terminal state. |

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
