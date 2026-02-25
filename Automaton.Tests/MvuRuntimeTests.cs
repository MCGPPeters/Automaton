// =============================================================================
// MVU Runtime Tests
// =============================================================================
// Proves the counter automaton works as an MVU application:
// dispatch events → state transitions → views rendered → effects handled.
// =============================================================================

using Automaton.Mvu;

namespace Automaton.Tests;

public class MvuRuntimeTests
{
    /// <summary>
    /// Simple view: just renders the count as a string.
    /// In real Abies this would be a Document (virtual DOM).
    /// </summary>
    private static string RenderCounter(CounterState state) =>
        $"Count: {state.Count}";

    /// <summary>
    /// Effect handler that collects log messages and produces no follow-up events.
    /// </summary>
    private static Task<IEnumerable<CounterEvent>> HandleEffect(CounterEffect effect) =>
        Task.FromResult<IEnumerable<CounterEvent>>(effect switch
        {
            CounterEffect.Log log => [],
            CounterEffect.None => [],
            _ => []
        });

    [Fact]
    public async Task Init_ProducesZeroState_AndRendersInitialView()
    {
        var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
            .Start(RenderCounter, HandleEffect);

        Assert.Equal(0, runtime.State.Count);
        Assert.Single(runtime.Views);
        Assert.Equal("Count: 0", runtime.Views[0]);
    }

    [Fact]
    public async Task Dispatch_Increment_UpdatesStateAndRendersNewView()
    {
        var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
            .Start(RenderCounter, HandleEffect);

        await runtime.Dispatch(new CounterEvent.Increment());

        Assert.Equal(1, runtime.State.Count);
        Assert.Equal(2, runtime.Views.Count);
        Assert.Equal("Count: 1", runtime.Views[1]);
    }

    [Fact]
    public async Task Dispatch_MultipleEvents_ProducesCorrectSequenceOfViews()
    {
        var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
            .Start(RenderCounter, HandleEffect);

        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Decrement());

        Assert.Equal(2, runtime.State.Count);
        Assert.Equal(5, runtime.Views.Count); // init + 4 dispatches
        Assert.Equal("Count: 0", runtime.Views[0]);
        Assert.Equal("Count: 1", runtime.Views[1]);
        Assert.Equal("Count: 2", runtime.Views[2]);
        Assert.Equal("Count: 3", runtime.Views[3]);
        Assert.Equal("Count: 2", runtime.Views[4]);
    }

    [Fact]
    public async Task Dispatch_Reset_ProducesLogEffect()
    {
        var logs = new List<string>();

        Task<IEnumerable<CounterEvent>> HandleEffectWithCapture(CounterEffect effect)
        {
            if (effect is CounterEffect.Log log)
            {
                logs.Add(log.Message);
            }

            return Task.FromResult<IEnumerable<CounterEvent>>([]);
        }

        var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
            .Start(RenderCounter, HandleEffectWithCapture);

        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Reset());

        Assert.Equal(0, runtime.State.Count);
        Assert.Single(logs);
        Assert.Equal("Counter reset from 3", logs[0]);
    }

    [Fact]
    public async Task Events_AreRecorded()
    {
        var runtime = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
            .Start(RenderCounter, HandleEffect);

        await runtime.Dispatch(new CounterEvent.Increment());
        await runtime.Dispatch(new CounterEvent.Decrement());

        Assert.Equal(2, runtime.Events.Count);
        Assert.IsType<CounterEvent.Increment>(runtime.Events[0]);
        Assert.IsType<CounterEvent.Decrement>(runtime.Events[1]);
    }
}
