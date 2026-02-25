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
// Event Sourcing is fundamentally command-driven: commands arrive, are validated
// against the current state via Decide, and only the resulting events are
// persisted. This makes ES the natural home of the Decider pattern.
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
    public void Append(TEvent @event) =>
        _events.Add(new StoredEvent<TEvent>(++_sequence, @event, DateTimeOffset.UtcNow));

    /// <summary>
    /// Replays all events through a fold function to rebuild state.
    /// </summary>
    public TState Replay<TState>(TState seed, Func<TState, TEvent, TState> fold) =>
        _events.Aggregate(seed, (state, stored) => fold(state, stored.Event));
}

/// <summary>
/// Runs a Decider as an event-sourced aggregate.
/// </summary>
/// <remarks>
/// <para>
/// Event Sourcing is fundamentally command-driven: commands arrive, are validated
/// against the current state via <c>Decide</c>, and only the resulting events
/// are persisted. This is the natural home of the Decider pattern.
/// </para>
/// <para>
/// The aggregate runner implements the decide-then-append pattern:
/// <list type="number">
///     <item>Receive a command (user intent)</item>
///     <item>Validate via <c>Decide(state, command)</c> → events or error</item>
///     <item>On success: transition state for each event, append to store</item>
///     <item>On failure: return error, state unchanged, nothing persisted</item>
/// </list>
/// </para>
/// <example>
/// <code>
/// var aggregate = AggregateRunner&lt;Counter, CounterState, CounterCommand,
///     CounterEvent, CounterEffect, CounterError&gt;.Create();
///
/// var result = aggregate.Handle(new CounterCommand.Add(3));
/// // result is Ok(CounterState { Count = 3 })
/// // aggregate.Store.Events.Count == 3  (3 Increment events)
///
/// var overflow = aggregate.Handle(new CounterCommand.Add(200));
/// // overflow is Err(CounterError.Overflow { ... })
/// // aggregate.State.Count is still 3 — nothing persisted
///
/// // Rebuild from scratch (simulates loading from disk)
/// var rebuilt = aggregate.Rebuild();
/// // rebuilt.Count == 3
/// </code>
/// </example>
/// </remarks>
public sealed class AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError>
    where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError>
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

    /// <summary>
    /// Whether the aggregate has reached a terminal state.
    /// </summary>
    public bool IsTerminal => TDecider.IsTerminal(_state);

    private AggregateRunner(TState state, EventStore<TEvent> store)
    {
        _state = state;
        _store = store;
    }

    /// <summary>
    /// Creates a new aggregate from its initial state.
    /// </summary>
    public static AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError> Create()
    {
        var (state, _) = TDecider.Init();
        return new AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError>(
            state, new EventStore<TEvent>());
    }

    /// <summary>
    /// Creates an aggregate and hydrates it from an existing event store.
    /// </summary>
    /// <remarks>
    /// Stored events are already validated facts — they are replayed through
    /// <c>Transition</c> without re-validation via <c>Decide</c>.
    /// </remarks>
    public static AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError> FromStore(
        EventStore<TEvent> store)
    {
        var (seed, _) = TDecider.Init();
        var state = store.Replay(seed, (s, e) => TDecider.Transition(s, e).State);
        return new AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError>(state, store);
    }

    /// <summary>
    /// Handles a command: validates via <c>Decide</c>, then transitions and persists
    /// each produced event on success. Returns the new state or an error.
    /// </summary>
    /// <remarks>
    /// On error, the aggregate state is unchanged and no events are appended.
    /// On success, all events are appended atomically and the state reflects
    /// the full sequence of transitions.
    /// </remarks>
    public Result<TState, TError> Handle(TCommand command) =>
        TDecider.Decide(_state, command).Match<Result<TState, TError>>(
            events =>
            {
                foreach (var @event in events)
                {
                    var (newState, effect) = TDecider.Transition(_state, @event);
                    _state = newState;
                    _store.Append(@event);
                    _effects.Add(effect);
                }

                return new Result<TState, TError>.Ok(_state);
            },
            error => new Result<TState, TError>.Err(error));

    /// <summary>
    /// Rebuilds state from scratch by replaying all stored events.
    /// </summary>
    public TState Rebuild()
    {
        var (seed, _) = TDecider.Init();
        _state = _store.Replay(seed, (s, e) => TDecider.Transition(s, e).State);
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
