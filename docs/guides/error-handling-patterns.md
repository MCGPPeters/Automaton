# Error Handling Patterns

How to work with `Result<TSuccess, TError>` — inspecting, mapping, chaining, and building pipelines.

## The Basics

`Result<TSuccess, TError>` is a discriminated union: either `Ok(value)` or `Err(error)`. It's a `readonly struct` — zero heap allocation.

```csharp
var ok = Result<int, string>.Ok(42);
var err = Result<int, string>.Err("something went wrong");
```

## Inspecting Results

Use `IsOk` / `IsErr` and `Value` / `Error`:

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

> ⚠️ Accessing `Value` on an `Err` or `Error` on an `Ok` throws `InvalidOperationException`. Always check first.

### With DecidingRuntime

```csharp
var result = await runtime.Handle(new CounterCommand.Add(5));

if (result.IsOk)
{
    var state = result.Value;
    return Ok($"Count is now {state.Count}");
}

return result.Error switch
{
    CounterError.Overflow o => BadRequest($"Would exceed max: {o.Current} + {o.Amount} > {o.Max}"),
    CounterError.Underflow u => BadRequest($"Would go below zero: {u.Current} + {u.Amount}"),
    CounterError.AlreadyAtZero => BadRequest("Counter is already at zero"),
    _ => InternalError("Unexpected error")
};
```

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

## LINQ Query Syntax

Result implements `Select` (functor) and `SelectMany` (monad), enabling LINQ query syntax:

```csharp
var result =
    from raw in parseInput(input)        // Result<RawData, Error>
    from valid in validate(raw)          // Result<ValidData, Error>
    select toCommand(valid);             // Result<Command, Error>
// Short-circuits on first Err
```

This is equivalent to the `Bind` chain above but can be more readable for multi-step pipelines.

### Combining Multiple Results

```csharp
var result =
    from user in FindUser(id)
    from account in GetAccount(user.AccountId)
    from balance in CheckBalance(account, amount)
    select new Transfer(user, account, balance);
```

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

    if (result.IsOk)
        return Ok(new { result.Value.Count });

    return result.Error switch
    {
        CounterError.Overflow => BadRequest("Counter would overflow"),
        CounterError.Underflow => BadRequest("Counter would underflow"),
        CounterError.AlreadyAtZero => Conflict("Counter is already at zero"),
        _ => StatusCode(500)
    };
}
```

### Validate Before Handle

```csharp
public async Task<IActionResult> AddToCounter(AddRequest request)
{
    var validation = ValidateRequest(request); // Result<CounterCommand, ValidationError>

    if (validation.IsErr)
        return BadRequest(validation.Error);

    var result = await runtime.Handle(validation.Value);

    if (result.IsOk)
        return Ok(new { result.Value.Count });

    return UnprocessableEntity(result.Error);
}
```

## Pipeline Error Handling

Observer and Interpreter pipelines return `Result<T, PipelineError>`. Handle dispatch errors:

```csharp
var dispatchResult = await runtime.Dispatch(new CounterEvent.Increment());

if (dispatchResult.IsErr)
{
    var error = dispatchResult.Error;
    logger.Error("Pipeline failed at {Source}: {Message}",
        error.Source, error.Message);

    if (error.Exception is not null)
        logger.Error(error.Exception, "Underlying exception");
}
```

### Recovering from Pipeline Errors

Use `Catch` on observers/interpreters to handle errors at the pipeline level:

```csharp
var resilientPipeline = persister
    .Catch(err => err.Source switch
    {
        "database" => Result<Unit, PipelineError>.Ok(Unit.Value), // retry later
        _ => Result<Unit, PipelineError>.Err(err) // propagate other errors
    });
```

## See Also

- [The Decider](../concepts/the-decider.md) — why Result exists and how Decide uses it
- [API Reference: Result](../reference/result.md) — complete method documentation
- [Upgrading to Decider](upgrading-to-decider.md) — adding command validation
