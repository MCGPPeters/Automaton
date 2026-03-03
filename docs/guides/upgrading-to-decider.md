# Upgrading to Decider

How to add command validation to an existing Automaton — a non-breaking upgrade.

## Why Upgrade?

The basic Automaton accepts any event:

```csharp
await runtime.Dispatch(new CounterEvent.Increment()); // always succeeds
```

The Decider adds a validation layer between user intent (commands) and facts (events):

```csharp
var result = await runtime.Handle(new CounterCommand.Add(200));
// result is Err(Overflow) — state unchanged, no events dispatched
```

## Step 1: Define Commands and Errors

Commands represent what the user wants to do. Errors represent why it can't happen:

```csharp
public interface CounterCommand
{
    record struct Add(int Amount) : CounterCommand;
    record struct Reset : CounterCommand;
}

public interface CounterError
{
    record struct Overflow(int Current, int Amount, int Max) : CounterError;
    record struct Underflow(int Current, int Amount) : CounterError;
    record struct AlreadyAtZero : CounterError;
}
```

**Tip:** Make errors carry context — not just "invalid" but *why* it's invalid.

## Step 2: Change the Interface

Replace `Automaton<...>` with `Decider<...>`:

```csharp
// Before
public class Counter : Automaton<CounterState, CounterEvent, CounterEffect, Unit>

// After
public class Counter
    : Decider<CounterState, CounterCommand, CounterEvent, CounterEffect, CounterError, Unit>
```

Because `Decider<...> : Automaton<...>`, your existing `Init` and `Transition` methods are still valid. Nothing breaks.

## Step 3: Add the Decide Function

```csharp
public static Result<CounterEvent[], CounterError> Decide(
    CounterState state, CounterCommand command) =>
    command switch
    {
        CounterCommand.Add(var n) when state.Count + n > MaxCount =>
            Result<CounterEvent[], CounterError>
                .Err(new CounterError.Overflow(state.Count, n, MaxCount)),

        CounterCommand.Add(var n) when n >= 0 =>
            Result<CounterEvent[], CounterError>
                .Ok(Enumerable.Repeat<CounterEvent>(
                    new CounterEvent.Increment(), n).ToArray()),

        // ... other cases

        _ => throw new UnreachableException()
    };
```

`Decide` is a pure function — no side effects, no async. Test it directly:

```csharp
var result = Counter.Decide(new CounterState(95), new CounterCommand.Add(10));
Assert.True(result.IsErr);
```

## Step 4: Choose Your Runtime

### Option A: DecidingRuntime (recommended)

Use `DecidingRuntime` for command-driven workflows:

```csharp
var runtime = await DecidingRuntime<Counter, CounterState, CounterCommand,
    CounterEvent, CounterEffect, CounterError, Unit>.Start(default, observer, interpreter);

var result = await runtime.Handle(new CounterCommand.Add(5));
```

### Option B: Keep AutomatonRuntime

Since `Decider : Automaton`, you can still use `AutomatonRuntime` and dispatch events directly. This is useful when some events come from commands (via Decide) and others from external sources:

```csharp
// Still works — Counter is still an Automaton
var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect, Unit>
    .Start(default, observer, interpreter);

await runtime.Dispatch(new CounterEvent.Increment()); // bypasses Decide
```

## Step 5: Add IsTerminal (Optional)

If your domain has a natural end-of-life, override `IsTerminal`:

```csharp
public static bool IsTerminal(CounterState state) => false; // default

// Or for domains with lifecycle:
public static bool IsTerminal(OrderState state) =>
    state.Status is OrderStatus.Shipped or OrderStatus.Cancelled;
```

## What Didn't Break

After upgrading:

- All existing `AutomatonRuntime` usage continues to work
- All existing tests that call `Transition` directly still pass
- All existing observers and interpreters are compatible
- The `DecidingRuntime` is a new *addition*, not a replacement

The Decider is strictly additive. This is by design — it follows the [Open/Closed Principle](https://en.wikipedia.org/wiki/Open%E2%80%93closed_principle): open for extension (new command validation), closed for modification (existing automaton behavior unchanged).

## See Also

- [The Decider](../concepts/the-decider.md) — conceptual explanation
- [Tutorial 05](../tutorials/05-command-validation.md) — full walkthrough
- [Error Handling Patterns](error-handling-patterns.md) — Result pipelines
