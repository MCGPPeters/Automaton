// =============================================================================
// Cross-Runtime Tests
// =============================================================================
// Proves the SAME automaton (Counter) produces identical results when executed
// by the MVU, Event Sourcing, and Actor runtimes.
//
// The transition function is the invariant. The runtime is the variable.
//
// MVU and Actor receive events directly (Automaton runtimes).
// ES receives commands (Decider runtime) — commands are validated by Decide,
// producing the same events that MVU/Actor receive directly.
// =============================================================================

using Automaton.Actor;
using Automaton.EventSourcing;
using Automaton.Mvu;

namespace Automaton.Tests;

public class CrossRuntimeTests
{
    /// <summary>
    /// The canonical event sequence used by MVU and Actor runtimes.
    /// </summary>
    private static readonly CounterEvent[] _eventScenario =
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
    /// The equivalent command sequence used by the ES runtime.
    /// Commands express intent; Decide validates and produces the same events.
    /// </summary>
    private static readonly CounterCommand[] _commandScenario =
    [
        new CounterCommand.Add(3),   // → 3 Increment events
        new CounterCommand.Add(-1),  // → 1 Decrement event
        new CounterCommand.Add(1),   // → 1 Increment event
        new CounterCommand.Reset(),  // → 1 Reset event
        new CounterCommand.Add(1)    // → 1 Increment event
    ];

    /// <summary>
    /// Expected final state: inc(3) - dec(1) + inc(1) = 3, reset → 0, inc(1) = 1
    /// </summary>
    private const int _expectedFinalCount = 1;

    [Fact]
    public async Task AllThreeRuntimes_ProduceIdenticalFinalState()
    {
        // --- MVU (receives events) ---
        var mvu = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
            .Start(s => $"Count: {s.Count}", _ => Task.FromResult<IEnumerable<CounterEvent>>([]));

        foreach (var e in _eventScenario)
        {
            await mvu.Dispatch(e);
        }

        // --- Event Sourcing (receives commands) ---
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        foreach (var cmd in _commandScenario)
        {
            aggregate.Handle(cmd);
        }

        // --- Actor (receives events) ---
        var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>.Spawn("cross-test");

        foreach (var e in _eventScenario)
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
        // Run through MVU (events)
        var mvu = await MvuRuntime<Counter, CounterState, CounterEvent, CounterEffect, string>
            .Start(s => s.Count.ToString(), _ => Task.FromResult<IEnumerable<CounterEvent>>([]));

        foreach (var e in _eventScenario)
        {
            await mvu.Dispatch(e);
        }

        // Run through ES (commands) and rebuild from scratch
        var aggregate = AggregateRunner<Counter, CounterState, CounterCommand,
            CounterEvent, CounterEffect, CounterError>.Create();

        foreach (var cmd in _commandScenario)
        {
            aggregate.Handle(cmd);
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

        var finalState = _eventScenario.Aggregate(seed, (state, @event) =>
            Counter.Transition(state, @event).State);

        Assert.Equal(_expectedFinalCount, finalState.Count);
    }
}
