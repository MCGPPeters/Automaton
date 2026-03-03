# Automaton.Resilience

Production-grade resilience strategies modeled as Mealy machine automata.

## Strategies

| Strategy | Description |
|----------|-------------|
| **Retry** | Retries a failed operation with configurable backoff (constant, linear, exponential, decorrelated jitter) |
| **Timeout** | Cancels an operation after a deadline |
| **Circuit Breaker** | Prevents cascading failures by short-circuiting calls to unhealthy dependencies |
| **Fallback** | Provides an alternative value when an operation fails |
| **Rate Limiter** | Constrains throughput to protect downstream services |
| **Hedging** | Sends parallel requests and takes the first success |

## Architecture

Each strategy is implemented as a **Mealy machine automaton** — a pure state machine with observable transitions. This gives you:

- **Full observability** — subscribe to state transitions, wire up OpenTelemetry
- **Testability** — test the state machine logic without real I/O
- **Composability** — combine strategies via the `Then` combinator

On top of the state machines, an ergonomic `Execute()` API provides the common case: wrap a delegate, get a `Result<T, ResilienceError>`.

## Quick Start

```csharp
using Automaton.Resilience;
using Automaton.Resilience.Retry;

// Simple retry with exponential backoff
var result = await Retry.Execute(
    async ct => await httpClient.GetStringAsync(url, ct),
    new RetryOptions(MaxAttempts: 3, Backoff: BackoffType.Exponential));

if (result.IsOk)
    Console.WriteLine(result.Value);
else
    Console.WriteLine($"Failed after retries: {result.Error}");
```

## OpenTelemetry

Register the activity source to collect spans:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(ResilienceDiagnostics.SourceName));
```
