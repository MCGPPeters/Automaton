# Error Handling Patterns

How to work with `Result<TSuccess, TError>` — matching, mapping, chaining, and building pipelines.

## The Basics

`Result<TSuccess, TError>` is a discriminated union: either `Ok(value)` or `Err(error)`. It's a `readonly struct` — zero heap allocation.

```csharp
var ok = Result<int, string>.Ok(42);
var err = Result<int, string>.Err("something went wrong");
```

## Pattern Matching with Match

`Match` forces you to handle both cases:

```csharp
var message = result.Match(
    value => $"Success: {value}",
    error => $"Failed: {error}");
```

### Async Match

```csharp
var response = await result.Match(
    async events => await persistEventsAsync(events),
    async error => await logErrorAsync(error));
```

### With DecidingRuntime

```csharp
var result = await runtime.Handle(new CounterCommand.Add(5));

var response = result.Match(
    state => Ok($"Count is now {state.Count}"),
    error => error switch
    {
        CounterError.Overflow o => BadRequest($"Would exceed max: {o.Current} + {o.Amount} > {o.Max}"),
        CounterError.Underflow u => BadRequest($"Would go below zero: {u.Current} + {u.Amount}"),
        CounterError.AlreadyAtZero => BadRequest("Counter is already at zero"),
        _ => InternalError("Unexpected error")
    });
```

## Checking Without Matching

For simple checks, use `IsOk` / `IsErr` and `Value` / `Error`:

```csharp
if (result.IsOk)
{
    var events = result.Value; // safe — we checked IsOk
    // ...
}

if (result.IsErr)
{
    var error = result.Error; // safe — we checked IsErr
    // ...
}
```

> ⚠️ Accessing `Value` on an `Err` or `Error` on an `Ok` throws `InvalidOperationException`. Always check first, or use `Match`.

## Transforming with Map

`Map` transforms the success value, leaving errors untouched:

```csharp
Result<int, string> ok = Result<int, string>.Ok(21);

Result<int, string> doubled = ok.Map(v => v * 2);
// Ok(42)

Result<int, string> err = Result<int, string>.Err("fail");
Result<int, string> stillErr = err.Map(v => v * 2);
// Err("fail") — Map skips the error case
```

### Practical Example

Transform validated events into a count:

```csharp
var eventCount = Counter.Decide(state, command)
    .Map(events => events.Length);
// Result<int, CounterError>
```

## Chaining with Bind

`Bind` chains operations that can themselves fail — the core of railway-oriented programming:

```csharp
Result<int, string> parse(string input) =>
    int.TryParse(input, out var n)
        ? Result<int, string>.Ok(n)
        : Result<int, string>.Err($"'{input}' is not a number");

Result<int, string> validate(int n) =>
    n > 0
        ? Result<int, string>.Ok(n)
        : Result<int, string>.Err($"{n} must be positive");

var result = parse("42").Bind(validate);
// Ok(42)

var result2 = parse("abc").Bind(validate);
// Err("'abc' is not a number") — validate is never called
```

If any step fails, the error short-circuits through the rest of the pipeline.

### Pipeline Pattern

```csharp
var result = parseInput(raw)          // Result<RawData, Error>
    .Map(normalize)                    // Result<NormalizedData, Error>
    .Bind(validate)                    // Result<ValidData, Error>
    .Map(toCommand)                    // Result<Command, Error>
    .Bind(cmd => decide(state, cmd));  // Result<Events, Error>
```

Each step only runs if the previous one succeeded. Errors propagate automatically.

## Transforming Errors with MapError

`MapError` transforms the error value, leaving success untouched:

```csharp
Result<int, string> err = Result<int, string>.Err("fail");

Result<int, int> mapped = err.MapError(e => e.Length);
// Err(4)
```

### Unifying Error Types

When different steps produce different error types, use `MapError` to unify them:

```csharp
// Step 1 returns ParseError, Step 2 returns ValidationError
// Unify into DomainError:

var result = parseInput(raw)
    .MapError(e => new DomainError.Parse(e.Message))
    .Bind(validate)
    .MapError(e => new DomainError.Validation(e.Message));
```

## Patterns for HTTP APIs

### Map Result to HTTP Response

```csharp
public async Task<IActionResult> HandleCommand(CounterCommand command)
{
    var result = await runtime.Handle(command);

    return result.Match<IActionResult>(
        state => Ok(new { state.Count }),
        error => error switch
        {
            CounterError.Overflow => BadRequest("Counter would overflow"),
            CounterError.Underflow => BadRequest("Counter would underflow"),
            CounterError.AlreadyAtZero => Conflict("Counter is already at zero"),
            _ => StatusCode(500)
        });
}
```

### Validate Before Handle

```csharp
public async Task<IActionResult> AddToCounter(AddRequest request)
{
    return ValidateRequest(request)                    // Result<CounterCommand, ValidationError>
        .MapError(e => (IActionResult)BadRequest(e))   // Result<CounterCommand, IActionResult>
        .Match(
            async cmd =>
            {
                var result = await runtime.Handle(cmd);
                return result.Match<IActionResult>(
                    state => Ok(new { state.Count }),
                    error => UnprocessableEntity(error));
            },
            error => Task.FromResult(error));
}
```

## See Also

- [The Decider](../concepts/the-decider.md) — why Result exists and how Decide uses it
- [API Reference: Result](../reference/result.md) — complete method documentation
- [Upgrading to Decider](upgrading-to-decider.md) — adding command validation
