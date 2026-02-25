// =============================================================================
// Decider Tests
// =============================================================================
// Tests the Decider pattern: command validation, error rejection, state
// invariants, and Result<TSuccess, TError> algebraic operations.
// =============================================================================

namespace Automaton.Tests;

public class DeciderTests
{
    private static readonly Observer<CounterState, CounterEvent, CounterEffect> NoOpObserver =
        (_, _, _) => Task.CompletedTask;

    private static readonly Interpreter<CounterEffect, CounterEvent> NoOpInterpreter =
        _ => Task.FromResult<IEnumerable<CounterEvent>>([]);

    // =========================================================================
    // DecidingRuntime — Command Handling
    // =========================================================================

    [Fact]
    public async Task Handle_ValidAdd_ProducesIncrementEventsAndUpdatesState()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new CounterCommand.Add(3));

        var ok = Assert.IsType<Result<CounterState, CounterError>.Ok>(result);
        Assert.Equal(3, ok.Value.Count);
        Assert.Equal(3, runtime.Events.Count);
        Assert.All(runtime.Events, e => Assert.IsType<CounterEvent.Increment>(e));
    }

    [Fact]
    public async Task Handle_NegativeAdd_ProducesDecrementEvents()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new CounterCommand.Add(5));
        var result = await runtime.Handle(new CounterCommand.Add(-2));

        var ok = Assert.IsType<Result<CounterState, CounterError>.Ok>(result);
        Assert.Equal(3, ok.Value.Count);
    }

    [Fact]
    public async Task Handle_AddZero_ProducesNoEvents()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new CounterCommand.Add(5));
        var eventsBefore = runtime.Events.Count;

        var result = await runtime.Handle(new CounterCommand.Add(0));

        var ok = Assert.IsType<Result<CounterState, CounterError>.Ok>(result);
        Assert.Equal(5, ok.Value.Count);
        Assert.Equal(eventsBefore, runtime.Events.Count);
    }

    [Fact]
    public async Task Handle_Overflow_ReturnsError()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new CounterCommand.Add(Counter.MaxCount + 1));

        var err = Assert.IsType<Result<CounterState, CounterError>.Err>(result);
        var overflow = Assert.IsType<CounterError.Overflow>(err.Error);
        Assert.Equal(0, overflow.Current);
        Assert.Equal(Counter.MaxCount + 1, overflow.Amount);
        Assert.Equal(Counter.MaxCount, overflow.Max);
        Assert.Equal(0, runtime.State.Count);
    }

    [Fact]
    public async Task Handle_Underflow_ReturnsError()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new CounterCommand.Add(-1));

        var err = Assert.IsType<Result<CounterState, CounterError>.Err>(result);
        Assert.IsType<CounterError.Underflow>(err.Error);
        Assert.Equal(0, runtime.State.Count);
    }

    [Fact]
    public async Task Handle_BoundaryValues_AcceptsExactMax()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new CounterCommand.Add(Counter.MaxCount));

        var ok = Assert.IsType<Result<CounterState, CounterError>.Ok>(result);
        Assert.Equal(Counter.MaxCount, ok.Value.Count);
    }

    [Fact]
    public async Task Handle_BoundaryValues_RejectsMaxPlusOne()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new CounterCommand.Add(Counter.MaxCount));
        var result = await runtime.Handle(new CounterCommand.Add(1));

        Assert.IsType<Result<CounterState, CounterError>.Err>(result);
    }

    [Fact]
    public async Task Handle_Reset_ProducesResetEventAndResetsState()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new CounterCommand.Add(5));
        var result = await runtime.Handle(new CounterCommand.Reset());

        var ok = Assert.IsType<Result<CounterState, CounterError>.Ok>(result);
        Assert.Equal(0, ok.Value.Count);
    }

    [Fact]
    public async Task Handle_ResetAtZero_ReturnsAlreadyAtZeroError()
    {
        var runtime = await CreateRuntime();

        var result = await runtime.Handle(new CounterCommand.Reset());

        var err = Assert.IsType<Result<CounterState, CounterError>.Err>(result);
        Assert.IsType<CounterError.AlreadyAtZero>(err.Error);
    }

    [Fact]
    public async Task Handle_ErrorDoesNotMutateState()
    {
        var runtime = await CreateRuntime();

        await runtime.Handle(new CounterCommand.Add(50));
        var stateBeforeError = runtime.State;
        var eventCountBeforeError = runtime.Events.Count;

        await runtime.Handle(new CounterCommand.Add(51));

        Assert.Equal(stateBeforeError, runtime.State);
        Assert.Equal(eventCountBeforeError, runtime.Events.Count);
    }

    [Fact]
    public async Task Handle_ObserverSeesAllTransitions()
    {
        var observed = new List<(CounterState State, CounterEvent Event, CounterEffect Effect)>();
        Observer<CounterState, CounterEvent, CounterEffect> observer = (state, @event, effect) =>
        {
            observed.Add((state, @event, effect));
            return Task.CompletedTask;
        };

        var runtime = await DecidingRuntime<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Start(observer, NoOpInterpreter);

        await runtime.Handle(new CounterCommand.Add(2));

        Assert.Equal(2, observed.Count);
        Assert.Equal(1, observed[0].State.Count);
        Assert.Equal(2, observed[1].State.Count);
    }

    [Fact]
    public async Task Handle_ObserverNotCalledOnError()
    {
        var observerCallCount = 0;
        Observer<CounterState, CounterEvent, CounterEffect> observer = (_, _, _) =>
        {
            observerCallCount++;
            return Task.CompletedTask;
        };

        var runtime = await DecidingRuntime<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Start(observer, NoOpInterpreter);

        await runtime.Handle(new CounterCommand.Add(-1));

        Assert.Equal(0, observerCallCount);
    }

    [Fact]
    public async Task IsTerminal_DefaultsFalse()
    {
        var runtime = await CreateRuntime();

        Assert.False(runtime.IsTerminal);

        await runtime.Handle(new CounterCommand.Add(50));

        Assert.False(runtime.IsTerminal);
    }

    // =========================================================================
    // Decide — Pure Function Tests (no runtime needed)
    // =========================================================================

    [Fact]
    public void Decide_IsPure_SameInputProducesSameOutput()
    {
        var state = new CounterState(5);
        var command = (CounterCommand)new CounterCommand.Add(3);

        var result1 = Counter.Decide(state, command);
        var result2 = Counter.Decide(state, command);

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void Decide_ValidAdd_ReturnsOkWithCorrectEventCount()
    {
        var state = new CounterState(0);

        var result = Counter.Decide(state, new CounterCommand.Add(5));

        var ok = Assert.IsType<Result<IEnumerable<CounterEvent>, CounterError>.Ok>(result);
        Assert.Equal(5, ok.Value.Count());
        Assert.All(ok.Value, e => Assert.IsType<CounterEvent.Increment>(e));
    }

    [Fact]
    public void Decide_NegativeAdd_ReturnsDecrementEvents()
    {
        var state = new CounterState(10);

        var result = Counter.Decide(state, new CounterCommand.Add(-3));

        var ok = Assert.IsType<Result<IEnumerable<CounterEvent>, CounterError>.Ok>(result);
        Assert.Equal(3, ok.Value.Count());
        Assert.All(ok.Value, e => Assert.IsType<CounterEvent.Decrement>(e));
    }

    // =========================================================================
    // Result<TSuccess, TError> — Algebraic Operations
    // =========================================================================

    [Fact]
    public void Result_Ok_IsOk()
    {
        var result = new Result<int, string>.Ok(42);

        Assert.True(result.IsOk);
        Assert.False(result.IsErr);
    }

    [Fact]
    public void Result_Err_IsErr()
    {
        var result = new Result<int, string>.Err("oops");

        Assert.False(result.IsOk);
        Assert.True(result.IsErr);
    }

    [Fact]
    public void Result_Match_DispatchesCorrectly()
    {
        Result<int, string> ok = new Result<int, string>.Ok(42);
        Result<int, string> err = new Result<int, string>.Err("fail");

        Assert.Equal("42", ok.Match(v => v.ToString(), e => e));
        Assert.Equal("fail", err.Match(v => v.ToString(), e => e));
    }

    [Fact]
    public void Result_Map_TransformsSuccess()
    {
        Result<int, string> ok = new Result<int, string>.Ok(21);

        var mapped = ok.Map(v => v * 2);

        var result = Assert.IsType<Result<int, string>.Ok>(mapped);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Result_Map_PreservesError()
    {
        Result<int, string> err = new Result<int, string>.Err("fail");

        var mapped = err.Map(v => v * 2);

        var result = Assert.IsType<Result<int, string>.Err>(mapped);
        Assert.Equal("fail", result.Error);
    }

    [Fact]
    public void Result_Bind_ChainsSuccess()
    {
        Result<int, string> ok = new Result<int, string>.Ok(21);

        var bound = ok.Bind(v => new Result<string, string>.Ok($"value: {v * 2}"));

        var result = Assert.IsType<Result<string, string>.Ok>(bound);
        Assert.Equal("value: 42", result.Value);
    }

    [Fact]
    public void Result_Bind_ShortCircuitsOnError()
    {
        Result<int, string> err = new Result<int, string>.Err("fail");

        var bound = err.Bind(v => new Result<string, string>.Ok($"value: {v}"));

        var result = Assert.IsType<Result<string, string>.Err>(bound);
        Assert.Equal("fail", result.Error);
    }

    [Fact]
    public void Result_MapError_TransformsError()
    {
        Result<int, string> err = new Result<int, string>.Err("fail");

        var mapped = err.MapError(e => e.Length);

        var result = Assert.IsType<Result<int, int>.Err>(mapped);
        Assert.Equal(4, result.Error);
    }

    [Fact]
    public void Result_MapError_PreservesSuccess()
    {
        Result<int, string> ok = new Result<int, string>.Ok(42);

        var mapped = ok.MapError(e => e.Length);

        var result = Assert.IsType<Result<int, int>.Ok>(mapped);
        Assert.Equal(42, result.Value);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static Task<DecidingRuntime<Counter, CounterState, CounterCommand,
        CounterEvent, CounterEffect, CounterError>> CreateRuntime() =>
        DecidingRuntime<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Start(NoOpObserver, NoOpInterpreter);
}
