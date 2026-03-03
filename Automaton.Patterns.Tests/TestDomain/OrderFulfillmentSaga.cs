// =============================================================================
// Order Fulfillment Saga — Test Domain for Saga Tests
// =============================================================================
// A simple 3-step order fulfillment saga:
//
//     AwaitingPayment → PaymentReceived → Shipping → OrderShipped → Completed
//
// Also supports cancellation and compensation:
//     Any non-terminal state → OrderCancelled → Cancelled
// =============================================================================

using Automaton.Patterns.Saga;

namespace Automaton.Patterns.Tests.TestDomain;

/// <summary>
/// The saga's progress state.
/// </summary>
public enum OrderSagaState
{
    AwaitingPayment,
    Shipping,
    Completed,
    Cancelled
}

/// <summary>
/// Domain events the saga reacts to.
/// </summary>
public interface OrderDomainEvent
{
    record struct PaymentReceived(string OrderId, decimal Amount) : OrderDomainEvent;
    record struct OrderShipped(string OrderId, string TrackingNumber) : OrderDomainEvent;
    record struct OrderCancelled(string OrderId, string Reason) : OrderDomainEvent;
}

/// <summary>
/// Commands/effects the saga produces for other aggregates.
/// </summary>
public interface FulfillmentCommand
{
    record struct None : FulfillmentCommand;
    record struct ShipOrder(string OrderId) : FulfillmentCommand;
    record struct SendConfirmation(string OrderId, string TrackingNumber) : FulfillmentCommand;
    record struct RefundPayment(string OrderId, decimal Amount) : FulfillmentCommand;
}

/// <summary>
/// Order fulfillment saga — coordinates payment → shipping → confirmation.
/// </summary>
public class OrderFulfillment
    : Saga<OrderSagaState, OrderDomainEvent, FulfillmentCommand, Unit>
{
    public static (OrderSagaState State, FulfillmentCommand Effect) Initialize(Unit _) =>
        (OrderSagaState.AwaitingPayment, new FulfillmentCommand.None());

    public static (OrderSagaState State, FulfillmentCommand Effect) Transition(
        OrderSagaState state, OrderDomainEvent @event) =>
        (state, @event) switch
        {
            (OrderSagaState.AwaitingPayment, OrderDomainEvent.PaymentReceived e) =>
                (OrderSagaState.Shipping, new FulfillmentCommand.ShipOrder(e.OrderId)),

            (OrderSagaState.Shipping, OrderDomainEvent.OrderShipped e) =>
                (OrderSagaState.Completed, new FulfillmentCommand.SendConfirmation(e.OrderId, e.TrackingNumber)),

            // Cancellation from any non-terminal state — compensate if payment was received
            (OrderSagaState.Shipping, OrderDomainEvent.OrderCancelled e) =>
                (OrderSagaState.Cancelled, new FulfillmentCommand.RefundPayment(e.OrderId, 0)),

            (OrderSagaState.AwaitingPayment, OrderDomainEvent.OrderCancelled _) =>
                (OrderSagaState.Cancelled, new FulfillmentCommand.None()),

            // Default: ignore unexpected events
            _ => (state, new FulfillmentCommand.None())
        };

    public static bool IsTerminal(OrderSagaState state) =>
        state is OrderSagaState.Completed or OrderSagaState.Cancelled;
}
