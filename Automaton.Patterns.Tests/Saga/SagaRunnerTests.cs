// =============================================================================
// SagaRunner Tests
// =============================================================================

using Automaton.Patterns.EventSourcing;
using Automaton.Patterns.Saga;
using Automaton.Patterns.Tests.TestDomain;

namespace Automaton.Patterns.Tests.Saga;

public class SagaRunnerTests : IDisposable
{
    private readonly InMemoryEventStore<OrderDomainEvent> _store = new();
    private const string _streamId = "saga-order-42";
    private SagaRunner<OrderFulfillment, OrderSagaState,
        OrderDomainEvent, FulfillmentCommand, Unit>? _saga;

    public void Dispose() => _saga?.Dispose();

    private SagaRunner<OrderFulfillment, OrderSagaState,
        OrderDomainEvent, FulfillmentCommand, Unit> CreateSaga() =>
        _saga = SagaRunner<OrderFulfillment, OrderSagaState,
            OrderDomainEvent, FulfillmentCommand, Unit>.Create(_store, _streamId, default);

    private async ValueTask<SagaRunner<OrderFulfillment, OrderSagaState,
        OrderDomainEvent, FulfillmentCommand, Unit>> LoadSaga() =>
        _saga = await SagaRunner<OrderFulfillment, OrderSagaState,
            OrderDomainEvent, FulfillmentCommand, Unit>.Load(_store, _streamId, default);

    // ── Create ──

    [Fact]
    public void Create_InitializesWithAwaitingPayment()
    {
        var saga = CreateSaga();

        Assert.Equal(OrderSagaState.AwaitingPayment, saga.State);
        Assert.Equal(0, saga.Version);
        Assert.Equal(_streamId, saga.StreamId);
        Assert.Empty(saga.Effects);
        Assert.False(saga.IsTerminal);
    }

    // ── Happy path: Payment → Ship → Complete ──

    [Fact]
    public async Task Handle_PaymentReceived_TransitionsToShippingAndProducesShipOrder()
    {
        var saga = CreateSaga();

        var effect = await saga.Handle(
            new OrderDomainEvent.PaymentReceived("order-42", 99.99m));

        Assert.Equal(OrderSagaState.Shipping, saga.State);
        Assert.IsType<FulfillmentCommand.ShipOrder>(effect);
        Assert.Equal("order-42", ((FulfillmentCommand.ShipOrder)effect).OrderId);
        Assert.Equal(1, saga.Version);
    }

    [Fact]
    public async Task Handle_OrderShipped_TransitionsToCompletedAndProducesSendConfirmation()
    {
        var saga = CreateSaga();

        await saga.Handle(new OrderDomainEvent.PaymentReceived("order-42", 99.99m));
        var effect = await saga.Handle(
            new OrderDomainEvent.OrderShipped("order-42", "TRACK-123"));

        Assert.Equal(OrderSagaState.Completed, saga.State);
        Assert.True(saga.IsTerminal);
        Assert.IsType<FulfillmentCommand.SendConfirmation>(effect);

        var confirmation = (FulfillmentCommand.SendConfirmation)effect;
        Assert.Equal("order-42", confirmation.OrderId);
        Assert.Equal("TRACK-123", confirmation.TrackingNumber);
        Assert.Equal(2, saga.Version);
    }

    // ── Terminal state ──

    [Fact]
    public async Task Handle_TerminalState_IgnoresEventAndReturnsInitEffect()
    {
        var saga = CreateSaga();

        await saga.Handle(new OrderDomainEvent.PaymentReceived("order-42", 99.99m));
        await saga.Handle(new OrderDomainEvent.OrderShipped("order-42", "TRACK-123"));

        Assert.True(saga.IsTerminal);

        // Further events should be ignored
        var effect = await saga.Handle(
            new OrderDomainEvent.PaymentReceived("order-42", 99.99m));

        Assert.IsType<FulfillmentCommand.None>(effect);
        Assert.Equal(OrderSagaState.Completed, saga.State);
        Assert.Equal(2, saga.Version); // No new events persisted
    }

    // ── Cancellation path ──

    [Fact]
    public async Task Handle_CancellationBeforePayment_TransitionsToCancelledWithNone()
    {
        var saga = CreateSaga();

        var effect = await saga.Handle(
            new OrderDomainEvent.OrderCancelled("order-42", "Customer changed mind"));

        Assert.Equal(OrderSagaState.Cancelled, saga.State);
        Assert.True(saga.IsTerminal);
        Assert.IsType<FulfillmentCommand.None>(effect);
    }

    [Fact]
    public async Task Handle_CancellationDuringShipping_ProducesRefund()
    {
        var saga = CreateSaga();

        await saga.Handle(new OrderDomainEvent.PaymentReceived("order-42", 99.99m));
        var effect = await saga.Handle(
            new OrderDomainEvent.OrderCancelled("order-42", "Out of stock"));

        Assert.Equal(OrderSagaState.Cancelled, saga.State);
        Assert.True(saga.IsTerminal);
        Assert.IsType<FulfillmentCommand.RefundPayment>(effect);
    }

    // ── Persistence ──

    [Fact]
    public async Task Handle_PersistsEventsToStream()
    {
        var saga = CreateSaga();

        await saga.Handle(new OrderDomainEvent.PaymentReceived("order-42", 99.99m));
        await saga.Handle(new OrderDomainEvent.OrderShipped("order-42", "TRACK-123"));

        var stream = _store.GetStream(_streamId);
        Assert.Equal(2, stream.Count);
        Assert.IsType<OrderDomainEvent.PaymentReceived>(stream[0].Event);
        Assert.IsType<OrderDomainEvent.OrderShipped>(stream[1].Event);
    }

    // ── Load / Replay ──

    [Fact]
    public async Task Load_ReplaysEventsToRebuildState()
    {
        var original = CreateSaga();
        await original.Handle(new OrderDomainEvent.PaymentReceived("order-42", 99.99m));
        original.Dispose();
        _saga = null;

        var reloaded = await LoadSaga();

        Assert.Equal(OrderSagaState.Shipping, reloaded.State);
        Assert.Equal(1, reloaded.Version);
    }

    [Fact]
    public async Task Load_EmptyStream_ReturnsInitialState()
    {
        var saga = await LoadSaga();

        Assert.Equal(OrderSagaState.AwaitingPayment, saga.State);
        Assert.Equal(0, saga.Version);
    }

    [Fact]
    public async Task Load_ThenHandleMore_ContinuesFromVersion()
    {
        var original = CreateSaga();
        await original.Handle(new OrderDomainEvent.PaymentReceived("order-42", 99.99m));
        original.Dispose();
        _saga = null;

        var reloaded = await LoadSaga();
        await reloaded.Handle(new OrderDomainEvent.OrderShipped("order-42", "TRACK-456"));

        Assert.Equal(OrderSagaState.Completed, reloaded.State);
        Assert.True(reloaded.IsTerminal);
        Assert.Equal(2, reloaded.Version);
    }

    // ── Effects tracking ──

    [Fact]
    public async Task Effects_TracksAllProducedEffects()
    {
        var saga = CreateSaga();

        await saga.Handle(new OrderDomainEvent.PaymentReceived("order-42", 99.99m));
        await saga.Handle(new OrderDomainEvent.OrderShipped("order-42", "TRACK-123"));

        Assert.Equal(2, saga.Effects.Count);
        Assert.IsType<FulfillmentCommand.ShipOrder>(saga.Effects[0]);
        Assert.IsType<FulfillmentCommand.SendConfirmation>(saga.Effects[1]);
    }

    // ── Thread safety ──

    [Fact]
    public async Task Handle_ConcurrentCalls_SerializesCorrectly()
    {
        // Create a saga that can handle many events before becoming terminal
        var store = new InMemoryEventStore<OrderDomainEvent>();
        using var saga = SagaRunner<OrderFulfillment, OrderSagaState,
            OrderDomainEvent, FulfillmentCommand, Unit>.Create(store, "saga-concurrent", default);

        // Only the first event matters (PaymentReceived), rest are ignored after terminal
        var firstEffect = await saga.Handle(
            new OrderDomainEvent.PaymentReceived("order-1", 10m));
        Assert.IsType<FulfillmentCommand.ShipOrder>(firstEffect);

        // Now saga is in Shipping state — ship it to complete
        var secondEffect = await saga.Handle(
            new OrderDomainEvent.OrderShipped("order-1", "TRACK-1"));
        Assert.IsType<FulfillmentCommand.SendConfirmation>(secondEffect);

        // Now it's terminal — fire 10 concurrent events, all should be ignored
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => saga.Handle(
                new OrderDomainEvent.PaymentReceived("order-1", 10m)).AsTask())
            .ToArray();

        var effects = await Task.WhenAll(tasks);
        Assert.All(effects, e => Assert.IsType<FulfillmentCommand.None>(e));
    }

    // ── Unexpected events ──

    [Fact]
    public async Task Handle_UnexpectedEvent_ProducesNoneEffect()
    {
        var saga = CreateSaga();

        // OrderShipped before payment — unexpected
        var effect = await saga.Handle(
            new OrderDomainEvent.OrderShipped("order-42", "TRACK-123"));

        Assert.IsType<FulfillmentCommand.None>(effect);
        Assert.Equal(OrderSagaState.AwaitingPayment, saga.State);
    }
}
