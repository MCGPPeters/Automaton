// =============================================================================
// Shared Runtime Tests
// =============================================================================
// Proves the AutomatonRuntime correctly implements the monadic left fold
// with Observer and Interpreter extension points.
// =============================================================================

namespace Automaton.Tests;

public class RuntimeTests
{
    /// <summary>
    /// No-op observer for tests that don't need observation.
    /// </summary>
    private static readonly Observer<CounterState, CounterEvent, CounterEffect> _noOpObserver =
        (_, _, _) => Task.CompletedTask;

    /// <summary>
    /// No-op interpreter for tests that don't need effect interpretation.
    /// </summary>
    private static readonly Interpreter<CounterEffect, CounterEvent> _noOpInterpreter =
        _ => Task.FromResult<IEnumerable<CounterEvent>>([]);

    [Fact]
    public async Task Dispatch_UpdatesState()
    {
        var runtime = new AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>(
            new CounterState(0), _noOpObserver, _noOpInterpreter);

        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Decrement());

        Assert.Equal(1, runtime.State.Count);
    }

    [Fact]
    public async Task Observer_ReceivesCorrectArguments()
    {
        var observed = new List<(CounterState State, CounterEvent Event, CounterEffect Effect)>();

        Observer<CounterState, CounterEvent, CounterEffect> capture = (state, @event, effect) =>
        {
            observed.Add((state, @event, effect));
            return Task.CompletedTask;
        };

        var runtime = new AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>(
            new CounterState(0), capture, _noOpInterpreter);

        await runtime.Dispatch(new CounterEvent.Increment());

        Assert.Single(observed);
        Assert.Equal(new CounterState(1), observed[0].State);
        Assert.IsType<CounterEvent.Increment>(observed[0].Event);
        Assert.IsType<CounterEffect.None>(observed[0].Effect);
    }

    [Fact]
    public async Task Interpreter_FeedbackEventsAreDispatched()
    {
        var feedbackCount = 0;

        // Interpreter: on Reset effect, produce an Increment feedback event (once)
        Interpreter<CounterEffect, CounterEvent> interpreter = effect =>
        {
            if (effect is CounterEffect.Log && feedbackCount == 0)
            {
                feedbackCount++;
                return Task.FromResult<IEnumerable<CounterEvent>>([new CounterEvent.Increment()]);
            }

            return Task.FromResult<IEnumerable<CounterEvent>>([]);
        };

        var runtime = new AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>(
            new CounterState(0), _noOpObserver, interpreter);

        // Increment to 3, then reset (produces Log effect → interpreter returns Increment)
        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Reset());

        // Reset to 0, then feedback Increment to 1
        Assert.Equal(1, runtime.State.Count);
    }

    [Fact]
    public async Task ObserverComposition_Then_BothObserversAreCalled()
    {
        var firstCalls = 0;
        var secondCalls = 0;

        Observer<CounterState, CounterEvent, CounterEffect> first = (_, _, _) =>
        {
            firstCalls++;
            return Task.CompletedTask;
        };

        Observer<CounterState, CounterEvent, CounterEffect> second = (_, _, _) =>
        {
            secondCalls++;
            return Task.CompletedTask;
        };

        var combined = first.Then(second);

        var runtime = new AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>(
            new CounterState(0), combined, _noOpInterpreter);

        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Increment());

        Assert.Equal(2, firstCalls);
        Assert.Equal(2, secondCalls);
    }

    [Fact]
    public void Reset_ReplacesStateWithoutTransition()
    {
        var runtime = new AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>(
            new CounterState(0), _noOpObserver, _noOpInterpreter);

        runtime.Reset(new CounterState(42));

        Assert.Equal(42, runtime.State.Count);
        Assert.Empty(runtime.Events);
    }

    [Fact]
    public async Task Events_RecordedIncludingFeedback()
    {
        var feedbackCount = 0;

        Interpreter<CounterEffect, CounterEvent> interpreter = effect =>
        {
            if (effect is CounterEffect.Log && feedbackCount == 0)
            {
                feedbackCount++;
                return Task.FromResult<IEnumerable<CounterEvent>>([new CounterEvent.Increment()]);
            }

            return Task.FromResult<IEnumerable<CounterEvent>>([]);
        };

        var runtime = new AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>(
            new CounterState(0), _noOpObserver, interpreter);

        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Reset());

        // Events: Increment, Reset, Increment (feedback)
        Assert.Equal(3, runtime.Events.Count);
        Assert.IsType<CounterEvent.Increment>(runtime.Events[0]);
        Assert.IsType<CounterEvent.Reset>(runtime.Events[1]);
        Assert.IsType<CounterEvent.Increment>(runtime.Events[2]);
    }

    [Fact]
    public async Task Start_CreatesRuntimeAndInterpretsInitEffect()
    {
        var runtime = await AutomatonRuntime<Counter, CounterState, CounterEvent, CounterEffect>
            .Start(_noOpObserver, _noOpInterpreter);

        // Counter.Init() produces (Count=0, None) — no-op interpreter returns empty
        Assert.Equal(0, runtime.State.Count);
        Assert.Empty(runtime.Events);
    }
}
