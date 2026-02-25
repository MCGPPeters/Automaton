// =============================================================================
// Actor Runtime Tests
// =============================================================================
// Proves the SAME counter automaton works as a mailbox-based actor:
// messages arrive via channel, are processed sequentially, effects handled.
// =============================================================================

using Automaton.Actor;

namespace Automaton.Tests;

public class ActorTests
{
    [Fact]
    public async Task Actor_InitialState_IsZero()
    {
        var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>.Spawn("counter");

        Assert.Equal(0, actor.State.Count);

        await actor.Stop();
    }

    [Fact]
    public async Task Actor_ProcessesMessages_FromMailbox()
    {
        var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>.Spawn("counter");

        await actor.Ref.Tell(new CounterEvent.Increment());
        await actor.Ref.Tell(new CounterEvent.Increment());
        await actor.Ref.Tell(new CounterEvent.Increment());

        await actor.DrainMailbox();

        Assert.Equal(3, actor.State.Count);
        Assert.Equal(3, actor.ProcessedMessages.Count);

        await actor.Stop();
    }

    [Fact]
    public async Task Actor_ProcessesMessages_Sequentially()
    {
        var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>.Spawn("counter");

        // Send many messages concurrently
        var tasks = Enumerable.Range(0, 100)
            .Select(_ => actor.Ref.Tell(new CounterEvent.Increment()).AsTask());
        await Task.WhenAll(tasks);

        await actor.DrainMailbox();

        // Sequential processing guarantees correct final state
        Assert.Equal(100, actor.State.Count);

        await actor.Stop();
    }

    [Fact]
    public async Task Actor_HandlesEffects_ViaCallback()
    {
        var logs = new List<string>();

        var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>.Spawn(
            "counter",
            effectHandler: async (effect, self) =>
            {
                if (effect is CounterEffect.Log log)
                {
                    logs.Add(log.Message);
                }

                await Task.CompletedTask;
            });

        await actor.Ref.Tell(new CounterEvent.Increment());
        await actor.Ref.Tell(new CounterEvent.Increment());
        await actor.Ref.Tell(new CounterEvent.Reset());

        await actor.DrainMailbox();

        Assert.Equal(0, actor.State.Count);
        Assert.Single(logs);
        Assert.Equal("Counter reset from 2", logs[0]);

        await actor.Stop();
    }

    [Fact]
    public async Task ActorRef_HasName()
    {
        var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>.Spawn("my-counter");

        Assert.Equal("my-counter", actor.Ref.Name);

        await actor.Stop();
    }

    [Fact]
    public async Task Actor_Decrement_ProducesNegativeCount()
    {
        var actor = ActorInstance<Counter, CounterState, CounterEvent, CounterEffect>.Spawn("counter");

        await actor.Ref.Tell(new CounterEvent.Decrement());
        await actor.Ref.Tell(new CounterEvent.Decrement());

        await actor.DrainMailbox();

        Assert.Equal(-2, actor.State.Count);

        await actor.Stop();
    }
}
