// =============================================================================
// Event Sourcing Runtime
// =============================================================================
// Event Sourcing as a specialization of the Automaton kernel.
//
// ES is the automaton with persistence: every event is stored, and the current
// state is reconstructed by replaying the event stream through the transition
// function (a left fold):
//
//     state = events.Aggregate(init, transition)
//
// This runtime provides:
// - In-memory event store (append + replay)
// - Aggregate runner (load → decide → append)
// - Projections (read models built from event stream)
// =============================================================================

namespace Automaton.EventSourcing;

/// <summary>
/// An envelope wrapping a stored event with metadata.
/// </summary>
public readonly record struct StoredEvent<TEvent>(
    long SequenceNumber,
    TEvent Event,
    DateTimeOffset Timestamp);

/// <summary>
/// In-memory event store. Appends events and replays them to rebuild state.
/// </summary>
/// <remarks>
/// This is deliberately simple — a production store would use a database
/// (EventStoreDB, Marten, etc.) but the abstraction is identical.
/// </remarks>
public sealed class EventStore<TEvent>
{
    private readonly List<StoredEvent<TEvent>> _events = [];
    private long _sequence;

    /// <summary>
    /// All stored events in order.
    /// </summary>
    public IReadOnlyList<StoredEvent<TEvent>> Events => _events;

    /// <summary>
    /// Appends events to the store.
    /// </summary>
    public void Append(IEnumerable<TEvent> events)
    {
        foreach (var @event in events)
        {
            _events.Add(new StoredEvent<TEvent>(++_sequence, @event, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Appends a single event to the store.
    /// </summary>
    public void Append(TEvent @event)
    {
        _events.Add(new StoredEvent<TEvent>(++_sequence, @event, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Replays all events through a fold function to rebuild state.
    /// </summary>
    public TState Replay<TState>(TState seed, Func<TState, TEvent, TState> fold) =>
        _events.Aggregate(seed, (state, stored) => fold(state, stored.Event));
}

/// <summary>
/// Runs an automaton as an event-sourced aggregate.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate runner implements the decide-then-append pattern:
/// 1. Rebuild current state from event stream (left fold)
/// 2. Execute a command (transition) to produce new events + effects
/// 3. Append new events to the store
/// 4. Handle effects
/// </para>
/// <para>
/// The transition function IS the aggregate's decide function.
/// The event IS both the input and the persisted fact.
/// </para>
/// <example>
/// <code>
/// var aggregate = AggregateRunner&lt;Counter, CounterState, CounterEvent, CounterEffect&gt;.Create();
///
/// aggregate.Dispatch(new CounterEvent.Increment());
/// aggregate.Dispatch(new CounterEvent.Increment());
/// // aggregate.State.Count == 2
/// // aggregate.Store.Events.Count == 2
///
/// // Rebuild from scratch (simulates loading from disk)
/// var rebuilt = aggregate.Rebuild();
/// // rebuilt.Count == 2
/// </code>
/// </example>
/// </remarks>
public sealed class AggregateRunner<TAutomaton, TState, TEvent, TEffect>
    where TAutomaton : Automaton<TState, TEvent, TEffect>
{
    private readonly EventStore<TEvent> _store;
    private readonly List<TEffect> _effects = [];
    private TState _state;

    /// <summary>
    /// The current state of the aggregate (rebuilt from events).
    /// </summary>
    public TState State => _state;

    /// <summary>
    /// All effects produced during the aggregate's lifetime.
    /// </summary>
    public IReadOnlyList<TEffect> Effects => _effects;

    /// <summary>
    /// The underlying event store.
    /// </summary>
    public EventStore<TEvent> Store => _store;

    private AggregateRunner(TState state, EventStore<TEvent> store)
    {
        _state = state;
        _store = store;
    }

    /// <summary>
    /// Creates a new aggregate from its initial state.
    /// </summary>
    public static AggregateRunner<TAutomaton, TState, TEvent, TEffect> Create()
    {
        var (state, _) = TAutomaton.Init();
        return new AggregateRunner<TAutomaton, TState, TEvent, TEffect>(state, new EventStore<TEvent>());
    }

    /// <summary>
    /// Creates an aggregate and hydrates it from an existing event store.
    /// </summary>
    public static AggregateRunner<TAutomaton, TState, TEvent, TEffect> FromStore(EventStore<TEvent> store)
    {
        var (seed, _) = TAutomaton.Init();
        var state = store.Replay(seed, (s, e) => TAutomaton.Transition(s, e).State);
        return new AggregateRunner<TAutomaton, TState, TEvent, TEffect>(state, store);
    }

    /// <summary>
    /// Dispatches an event: transitions state, appends the event, and records effects.
    /// </summary>
    public void Dispatch(TEvent @event)
    {
        var (newState, effect) = TAutomaton.Transition(_state, @event);
        _state = newState;
        _store.Append(@event);
        _effects.Add(effect);
    }

    /// <summary>
    /// Rebuilds state from scratch by replaying all stored events.
    /// </summary>
    public TState Rebuild()
    {
        var (seed, _) = TAutomaton.Init();
        _state = _store.Replay(seed, (s, e) => TAutomaton.Transition(s, e).State);
        return _state;
    }
}

/// <summary>
/// A projection that builds a read model from an event stream.
/// </summary>
/// <remarks>
/// Projections are just folds over the event stream with a different accumulator.
/// They produce read-optimized views of the data.
/// </remarks>
/// <remarks>
/// Creates a projection with an initial read model and an apply function.
/// </remarks>
public sealed class Projection<TEvent, TReadModel>(TReadModel initial, Func<TReadModel, TEvent, TReadModel> apply)
{
    private readonly Func<TReadModel, TEvent, TReadModel> _apply = apply;

    /// <summary>
    /// The current read model.
    /// </summary>
    public TReadModel ReadModel { get; private set; } = initial;

    /// <summary>
    /// Projects all events from a store into the read model.
    /// </summary>
    public TReadModel Project(EventStore<TEvent> store)
    {
        ReadModel = store.Replay(ReadModel, _apply);
        return ReadModel;
    }

    /// <summary>
    /// Applies a single event to the read model.
    /// </summary>
    public TReadModel Apply(TEvent @event)
    {
        ReadModel = _apply(ReadModel, @event);
        return ReadModel;
    }
}
