// =============================================================================
// Saga — Process Manager as Mealy Machine
// =============================================================================
// A Saga (Process Manager) coordinates work across multiple aggregates by
// reacting to domain events and producing effects (typically commands to
// dispatch to other aggregates).
//
// Mathematically, a Saga IS an Automaton:
//
//     transition : (SagaState × DomainEvent) → (SagaState × SagaEffect)
//
// Where:
//     - DomainEvent = events from various aggregates the saga reacts to
//     - SagaEffect  = commands/actions to perform on other aggregates
//     - SagaState   = the saga's own progress tracking state
//
// The key insight: a Saga is an Automaton where the inputs are domain events
// and the outputs are commands. It doesn't validate commands (that's the
// aggregate's job) — it simply reacts to what happened and decides what to
// do next.
//
// Examples:
//     - Order fulfillment: PaymentReceived → ShipOrder → DeliveryConfirmed
//     - User onboarding: AccountCreated → SendWelcomeEmail → FirstLoginDetected
//     - Compensation: BookFlight → BookHotel (fails) → CancelFlight
// =============================================================================

namespace Automaton.Patterns.Saga;

/// <summary>
/// A Saga (Process Manager) that coordinates multi-aggregate workflows.
/// </summary>
/// <remarks>
/// <para>
/// The Saga pattern extends <see cref="Automaton{TState,TEvent,TEffect,TParameters}"/> with
/// lifecycle management via <see cref="IsTerminal"/>. A saga tracks the progress
/// of a multi-step business process and produces effects (commands) that should
/// be dispatched to the appropriate aggregates.
/// </para>
/// <para>
/// <b>Design guidance:</b>
/// <list type="bullet">
///     <item><description>
///         <c>TEvent</c> represents domain events from multiple aggregates
///         that this saga reacts to. Use a common base type or discriminated union.
///     </description></item>
///     <item><description>
///         <c>TEffect</c> represents the commands/actions to perform.
///         The saga runtime does NOT execute these — it returns them for
///         the caller to dispatch.
///     </description></item>
///     <item><description>
///         <c>TState</c> tracks the saga's progress. Include enough information
///         to determine what has happened and what should happen next.
///     </description></item>
///     <item><description>
///         The <c>Transition</c> function MUST be pure. All routing, retries,
///         and compensation logic should be expressed as state transitions
///         and effects, not as side effects.
///     </description></item>
/// </list>
/// </para>
/// <example>
/// <code>
/// // Order fulfillment saga
/// public class OrderFulfillment
///     : Saga&lt;OrderSagaState, OrderDomainEvent, FulfillmentCommand, Unit&gt;
/// {
///     public static (OrderSagaState, FulfillmentCommand) Init(Unit _) =&gt;
///         (OrderSagaState.AwaitingPayment, new FulfillmentCommand.None());
///
///     public static (OrderSagaState, FulfillmentCommand) Transition(
///         OrderSagaState state, OrderDomainEvent @event) =&gt;
///         (state, @event) switch
///         {
///             (OrderSagaState.AwaitingPayment, OrderDomainEvent.PaymentReceived e) =&gt;
///                 (OrderSagaState.Shipping, new FulfillmentCommand.ShipOrder(e.OrderId)),
///             (OrderSagaState.Shipping, OrderDomainEvent.OrderShipped e) =&gt;
///                 (OrderSagaState.Completed, new FulfillmentCommand.None()),
///             _ =&gt; (state, new FulfillmentCommand.None())
///         };
///
///     public static bool IsTerminal(OrderSagaState state) =&gt;
///         state is OrderSagaState.Completed or OrderSagaState.Cancelled;
/// }
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TState">The saga's progress state.</typeparam>
/// <typeparam name="TEvent">Domain events the saga reacts to.</typeparam>
/// <typeparam name="TEffect">Effects (commands) the saga produces.</typeparam>
/// <typeparam name="TParameters">The type of parameters passed to <see cref="Automaton{TState,TEvent,TEffect,TParameters}.Init"/>.</typeparam>
public interface Saga<TState, TEvent, TEffect, TParameters>
    : Automaton<TState, TEvent, TEffect, TParameters>
{
    /// <summary>
    /// Whether the saga has reached a terminal state (completed, cancelled, or failed).
    /// </summary>
    /// <remarks>
    /// Terminal sagas no longer process events. The runtime can use this
    /// for cleanup (removing subscriptions, archiving state).
    /// Defaults to <c>false</c> (never terminal).
    /// </remarks>
    static virtual bool IsTerminal(TState state) => false;
}
