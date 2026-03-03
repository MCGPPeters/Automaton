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
///     <item><description>Receive a command (user intent)</description></item>
///     <item><description>Validate via <c>Decide(state, command)</c> → events or error</description></item>
///     <item><description>On success: transition state for each event, append to store</description></item>
///     <item><description>On failure: return error, state unchanged, nothing persisted</description></item>
/// </list>
/// </para>
/// <example>
/// <code>
/// var aggregate = AggregateRunner&lt;Thermostat, ThermostatState, ThermostatCommand,
///     ThermostatEvent, ThermostatEffect, ThermostatError&gt;.Create();
///
/// var result = aggregate.Handle(new ThermostatCommand.RecordReading(18m));
/// // result is Ok(ThermostatState { CurrentTemp = 18, Heating = true })
/// // aggregate.Store.Events.Count == 2  (TemperatureRecorded + HeaterTurnedOn)
///
/// var invalid = aggregate.Handle(new ThermostatCommand.SetTarget(50m));
/// // invalid is Err(ThermostatError.InvalidTarget { ... })
/// // aggregate.State unchanged — nothing persisted
///
/// // Rebuild from scratch (simulates loading from disk)
/// var rebuilt = aggregate.Rebuild();
/// // rebuilt == aggregate.State
/// </code>
/// </example>
/// </remarks>
public sealed class AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>
    where TDecider : Decider<TState, TCommand, TEvent, TEffect, TError, TParameters>
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
    public static AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters> Create()
    {
        var (state, _) = TDecider.Init(default!);
        return new AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>(
            state, new EventStore<TEvent>());
    }

    /// <summary>
    /// Creates an aggregate and hydrates it from an existing event store.
    /// </summary>
    /// <remarks>
    /// Stored events are already validated facts — they are replayed through
    /// <c>Transition</c> without re-validation via <c>Decide</c>.
    /// </remarks>
    public static AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters> FromStore(
        EventStore<TEvent> store)
    {
        var (seed, _) = TDecider.Init(default!);
        var state = store.Replay(seed, (s, e) => TDecider.Transition(s, e).State);
        return new AggregateRunner<TDecider, TState, TCommand, TEvent, TEffect, TError, TParameters>(state, store);
    }

    /// <summary>
    /// Handles a command: validates via <c>Decide</c>, then transitions and persists
    /// each produced event on success. Returns the new state or an error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On validation error, the aggregate state is unchanged and no events are appended.
    /// </para>
    /// <para>
    /// On success, events are materialized and transitions are computed in isolation.
    /// State and effects are only committed after all transitions and store appends succeed,
    /// ensuring the state/store invariant is preserved on partial failure.
    /// </para>
    /// </remarks>
    public Result<TState, TError> Handle(TCommand command)
    {
        var decided = TDecider.Decide(_state, command);
        if (decided.IsOk)
        {
            var events = decided.Value;

            // Transition in a single pass over the materialized array.
            // Transition is pure (pattern-match exhaustive) so cannot throw
            // for well-formed domains. We compute new state and effects in
            // locals to preserve the state/store invariant on failure.
            var newState = _state;
            var newEffects = new List<TEffect>();

            for (var i = 0; i < events.Length; i++)
            {
                var (transitioned, effect) = TDecider.Transition(newState, events[i]);
                newState = transitioned;
                newEffects.Add(effect);
            }

            // Append the already-materialized array to the store.
            _store.Append(events);

            // Commit state and effects only after successful append.
            _state = newState;
            _effects.AddRange(newEffects);

            return Result<TState, TError>.Ok(_state);
        }
        else
        {
            return Result<TState, TError>.Err(decided.Error);
        }
    }

    /// <summary>
    /// Rebuilds state from scratch by replaying all stored events.
    /// </summary>
    public TState Rebuild()
    {
        var (seed, _) = TDecider.Init(default!);
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
