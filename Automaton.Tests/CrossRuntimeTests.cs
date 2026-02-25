// =============================================================================
// Cross-Runtime Tests
// =============================================================================
// Proves the SAME automaton (Counter) produces identical results when executed
// by the MVU, Event Sourcing, and Actor runtimes.
//
// This is the key insight: the transition function is the invariant.
// The runtime is the variable.
// =============================================================================

using Automaton.Actor;
using Automaton.EventSourcing;
using Automaton.Mvu;

namespace Automaton.Tests;

public class CrossRuntimeTests
{
    /// <summary>
    /// The canonical event sequence used across all runtimes.
    /// </summary>
    private static readonly CounterEvent[] _scenario =
    [
        new CounterEvent.Increment(),
        new CounterEvent.Increment(),
        new CounterEvent.Increment(),
        new CounterEvent.Decrement(),
        new CounterEvent.Increment(),
        new CounterEvent.Reset(),
        new CounterEvent.Increment()
    ];

    /// <summary>
    /// Expected final state: inc(3) - dec(1) + inc(1) = 3, reset → 0, inc(1) = 1
    /// </summary>
    private const int _expectedFinalCount = 1;

    [Fact]
    public async Task AllThreeRuntimes_ProduceIdenticalFinalState()
    {
        // --- MVU ---
        var mvu = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
            .Start(s => $"Count: {s.Count}", _ => Task.FromResult<IEnumerable<CounterEvent>>([]));;

        foreach (var e in _scenario)
        {
            await mvu.Dispatch(e);
        }

        // --- Event Sourcing ---
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        foreach (var e in _scenario)
        {
            await aggregate.Dispatch(e);
        }

        // --- Actor ---
        var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>.Spawn("cross-test");

        foreach (var e in _scenario)
        {
            await actor.Ref.Tell(e);
        }

        await actor.DrainMailbox();

        // --- All three produce identical state ---
        Assert.Equal(_expectedFinalCount, mvu.State.Count);
        Assert.Equal(_expectedFinalCount, aggregate.State.Count);
        Assert.Equal(_expectedFinalCount, actor.State.Count);

        // --- And they all equal each other ---
        Assert.Equal(mvu.State, aggregate.State);
        Assert.Equal(aggregate.State, actor.State);

        await actor.Stop();
    }

    [Fact]
    public async Task EventSourcing_CanRebuild_ToSameStateAsMvu()
    {
        // Run through MVU
        var mvu = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
            .Start(s => s.Count.ToString(), _ => Task.FromResult<IEnumerable<CounterEvent>>([]));;

        foreach (var e in _scenario)
        {
            await mvu.Dispatch(e);
        }

        // Run through ES and rebuild from scratch
        var aggregate = AggregateRunner<Counter, CounterState, CounterEvent, CounterEffect>.Create();

        foreach (var e in _scenario)
        {
            await aggregate.Dispatch(e);
        }

        var rebuilt = aggregate.Rebuild();

        Assert.Equal(mvu.State, rebuilt);
    }

    [Fact]
    public void AutomatonTransition_IsPure_SameInputProducesSameOutput()
    {
        var state = new CounterState(5);
        var @event = new CounterEvent.Increment();

        var (state1, effect1) = Counter.Transition(state, @event);
        var (state2, effect2) = Counter.Transition(state, @event);

        Assert.Equal(state1, state2);
        Assert.Equal(effect1, effect2);
    }

    [Fact]
    public void AutomatonTransition_IsALeftFold()
    {
        // state = events.Aggregate(init, transition)
        // This IS event sourcing. This IS MVU. This IS actor state.
        var (seed, _) = Counter.Init();

        var finalState = _scenario.Aggregate(seed, (state, @event) =>
            Counter.Transition(state, @event).State);

        Assert.Equal(_expectedFinalCount, finalState.Count);
    }
}
