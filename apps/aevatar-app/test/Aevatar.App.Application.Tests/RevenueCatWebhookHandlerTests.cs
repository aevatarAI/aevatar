using Aevatar.App.Application.Projection.Orchestration;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Stores;
using Aevatar.App.Application.Services;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.App.Application.Tests;

public sealed class RevenueCatWebhookHandlerTests
{
    private readonly TestActorFactory _actors = new();
    private readonly NoOpProjectionManager _projectionManager = new();
    private readonly FakeToltService _toltService = new();
    private readonly AppInMemoryDocumentStore<AppUserAffiliateReadModel, string> _affiliateStore = new(m => m.Id);
    private readonly AppInMemoryDocumentStore<AppPaymentTransactionReadModel, string> _transactionStore = new(m => m.Id);

    private RevenueCatWebhookHandler CreateHandler() => new(
        _actors,
        _projectionManager,
        _toltService,
        _affiliateStore,
        _transactionStore,
        NullLogger<RevenueCatWebhookHandler>.Instance);

    [Fact]
    public async Task HandleAsync_Ignores_MissingEvent()
    {
        var handler = CreateHandler();
        await handler.HandleAsync(new RevenueCatWebhookPayload { Event = null });
        _toltService.PaymentCalls.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_Ignores_MissingType()
    {
        var handler = CreateHandler();
        await handler.HandleAsync(Payload(type: null, txId: "tx1", userId: "u1"));
        _toltService.PaymentCalls.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_Ignores_MissingTransactionId()
    {
        var handler = CreateHandler();
        await handler.HandleAsync(Payload(type: "INITIAL_PURCHASE", txId: null, userId: "u1"));
        _toltService.PaymentCalls.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_Ignores_MissingUserId()
    {
        var handler = CreateHandler();
        await handler.HandleAsync(Payload(type: "INITIAL_PURCHASE", txId: "tx1", userId: null));
        _toltService.PaymentCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("INITIAL_PURCHASE")]
    [InlineData("RENEWAL")]
    [InlineData("NON_RENEWING_PURCHASE")]
    public async Task HandleAsync_Payment_CreatesTransaction(string eventType)
    {
        var handler = CreateHandler();
        var payload = Payload(
            type: eventType,
            txId: "tx-pay-1",
            userId: "user-1",
            price: 9.99m,
            priceLocal: 9.99m,
            currency: "USD",
            productId: "pro_monthly",
            store: "APP_STORE");

        await handler.HandleAsync(payload);

        var actorId = _actors.ResolveActorId<PaymentTransactionGAgent>("tx-pay-1");
        _projectionManager.EnsuredIds.Should().Contain(actorId);

        var agent = await _actors.GetOrCreateAgentAsync<PaymentTransactionGAgent>("tx-pay-1");
        agent.State.TransactionId.Should().Be("tx-pay-1");
        agent.State.UserId.Should().Be("user-1");
        agent.State.Amount.Should().Be(999);
        agent.State.Currency.Should().Be("USD");
        agent.State.ProductId.Should().Be("pro_monthly");
        agent.State.Store.Should().Be("APP_STORE");
        agent.State.CreatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_Payment_UsesPurchasedAtMs_AsCreatedAt()
    {
        var handler = CreateHandler();
        const long purchasedMs = 1658726374000;
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(purchasedMs);

        await handler.HandleAsync(Payload(
            type: "INITIAL_PURCHASE",
            txId: "tx-time-1",
            userId: "user-time",
            price: 9.99m,
            priceLocal: 9.99m,
            purchasedAtMs: purchasedMs));

        var agent = await _actors.GetOrCreateAgentAsync<PaymentTransactionGAgent>("tx-time-1");
        agent.State.CreatedAt.Should().NotBeNull();
        agent.State.CreatedAt!.ToDateTimeOffset().Should().Be(expected);
    }

    [Fact]
    public async Task HandleAsync_Payment_TracksAffiliate_WhenBound()
    {
        var affiliateActorId = _actors.ResolveActorId<UserAffiliateGAgent>("user-aff");
        await _affiliateStore.UpsertAsync(new AppUserAffiliateReadModel
        {
            Id = affiliateActorId,
            CustomerId = "cust-123",
            Platform = "tolt"
        });
        _toltService.NextPaymentResult = new ToltPaymentResult(true, "tolt-tx-abc", null);

        var handler = CreateHandler();
        await handler.HandleAsync(Payload(
            type: "INITIAL_PURCHASE",
            txId: "tx-aff-1",
            userId: "user-aff",
            price: 19.99m,
            priceLocal: 19.99m));

        _toltService.PaymentCalls.Should().Be(1);

        var agent = await _actors.GetOrCreateAgentAsync<PaymentTransactionGAgent>("tx-aff-1");
        agent.State.AffiliateTransactionId.Should().Be("tolt-tx-abc");
        agent.State.AffiliatePlatform.Should().Be("tolt");
    }

    [Fact]
    public async Task HandleAsync_Payment_SkipsAffiliate_WhenNotBound()
    {
        var handler = CreateHandler();
        await handler.HandleAsync(Payload(
            type: "INITIAL_PURCHASE",
            txId: "tx-no-aff",
            userId: "user-no-aff",
            price: 9.99m,
            priceLocal: 9.99m));

        _toltService.PaymentCalls.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_NonRenewing_SetsBillingTypeOneTime()
    {
        _toltService.NextPaymentResult = new ToltPaymentResult(true, "tolt-tx", null);
        var affiliateActorId = _actors.ResolveActorId<UserAffiliateGAgent>("user-ot");
        await _affiliateStore.UpsertAsync(new AppUserAffiliateReadModel
        {
            Id = affiliateActorId,
            CustomerId = "cust-ot",
            Platform = "tolt"
        });

        var handler = CreateHandler();
        await handler.HandleAsync(Payload(
            type: "NON_RENEWING_PURCHASE",
            txId: "tx-ot",
            userId: "user-ot",
            price: 49.99m,
            priceLocal: 49.99m));

        _toltService.LastBillingType.Should().Be("one_time");
    }

    [Fact]
    public async Task HandleAsync_Renewal_SetsBillingTypeSubscription()
    {
        _toltService.NextPaymentResult = new ToltPaymentResult(true, "tolt-tx", null);
        var affiliateActorId = _actors.ResolveActorId<UserAffiliateGAgent>("user-sub");
        await _affiliateStore.UpsertAsync(new AppUserAffiliateReadModel
        {
            Id = affiliateActorId,
            CustomerId = "cust-sub",
            Platform = "tolt"
        });

        var handler = CreateHandler();
        await handler.HandleAsync(Payload(
            type: "RENEWAL",
            txId: "tx-sub",
            userId: "user-sub",
            price: 9.99m,
            priceLocal: 9.99m));

        _toltService.LastBillingType.Should().Be("subscription");
    }

    [Fact]
    public async Task HandleAsync_Cancellation_WithCustomerSupport_RefundsTransaction()
    {
        var handler = CreateHandler();

        await handler.HandleAsync(Payload(
            type: "INITIAL_PURCHASE", txId: "tx-refund-1", userId: "user-r1",
            price: 9.99m, priceLocal: 9.99m));

        await handler.HandleAsync(Payload(
            type: "CANCELLATION", txId: "tx-refund-1", userId: "user-r1",
            cancelReason: "CUSTOMER_SUPPORT"));

        var actorId = _actors.ResolveActorId<PaymentTransactionGAgent>("tx-refund-1");
        _projectionManager.EnsuredIds.Should().Contain(actorId);

        var agent = await _actors.GetOrCreateAgentAsync<PaymentTransactionGAgent>("tx-refund-1");
        agent.State.Refunded.Should().BeTrue();
        agent.State.RefundedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_Cancellation_TracksRefund_WhenAffiliateTxExists()
    {
        var txActorId = _actors.ResolveActorId<PaymentTransactionGAgent>("tx-refund-2");
        await _transactionStore.UpsertAsync(new AppPaymentTransactionReadModel
        {
            Id = txActorId,
            AffiliateTransactionId = "aff-tx-to-refund"
        });

        var handler = CreateHandler();
        await handler.HandleAsync(Payload(
            type: "CANCELLATION",
            txId: "tx-refund-2",
            userId: "user-r2",
            cancelReason: "CUSTOMER_SUPPORT"));

        _toltService.RefundCalls.Should().Be(1);
        _toltService.LastRefundTxId.Should().Be("aff-tx-to-refund");
    }

    [Fact]
    public async Task HandleAsync_Cancellation_SkipsRefund_WhenNoAffiliateTx()
    {
        var handler = CreateHandler();
        await handler.HandleAsync(Payload(
            type: "CANCELLATION",
            txId: "tx-refund-3",
            userId: "user-r3",
            cancelReason: "CUSTOMER_SUPPORT"));

        _toltService.RefundCalls.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_Cancellation_NonCustomerSupport_IsIgnored()
    {
        var handler = CreateHandler();
        await handler.HandleAsync(Payload(
            type: "CANCELLATION",
            txId: "tx-cancel",
            userId: "user-c1",
            cancelReason: "UNSUBSCRIBE"));

        _projectionManager.EnsuredIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("PRODUCT_CHANGE")]
    [InlineData("BILLING_ISSUE")]
    [InlineData("SUBSCRIBER_ALIAS")]
    public async Task HandleAsync_UnhandledType_IsIgnored(string eventType)
    {
        var handler = CreateHandler();
        await handler.HandleAsync(Payload(type: eventType, txId: "tx-x", userId: "u-x"));

        _projectionManager.EnsuredIds.Should().BeEmpty();
        _toltService.PaymentCalls.Should().Be(0);
    }

    private static RevenueCatWebhookPayload Payload(
        string? type, string? txId, string? userId,
        decimal price = 0, decimal priceLocal = 0,
        string? currency = null, string? productId = null,
        string? store = null, string? cancelReason = null,
        long? purchasedAtMs = null) => new()
    {
        Event = new RevenueCatEvent
        {
            Type = type,
            TransactionId = txId,
            AppUserId = userId,
            Price = price,
            PriceInPurchasedCurrency = priceLocal,
            Currency = currency,
            ProductId = productId,
            Store = store,
            CancelReason = cancelReason,
            PurchasedAtMs = purchasedAtMs
        }
    };

    internal sealed class NoOpProjectionManager : IAppProjectionManager
    {
        public List<string> EnsuredIds { get; } = [];

        public Task EnsureSubscribedAsync(string actorId, CancellationToken ct = default)
        {
            EnsuredIds.Add(actorId);
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class FakeToltService : IToltAppService
    {
        public ToltPaymentResult NextPaymentResult { get; set; } = new(false, null, "not configured");
        public int PaymentCalls { get; private set; }
        public int RefundCalls { get; private set; }
        public string? LastBillingType { get; private set; }
        public string? LastRefundTxId { get; private set; }

        public Task<ToltClickResult?> TrackClickAsync(string refValue, string pageUrl, string? device)
            => Task.FromResult<ToltClickResult?>(null);

        public Task<ToltBindResult> BindReferralAsync(string email, string referralCode, string userId)
            => Task.FromResult(new ToltBindResult(false, null, "not configured"));

        public Task<ToltPaymentResult> TrackPaymentAsync(
            string customerId, int amountUsd, string billingType,
            string transactionId, string productId, string source)
        {
            PaymentCalls++;
            LastBillingType = billingType;
            return Task.FromResult(NextPaymentResult);
        }

        public Task<bool> TrackRefundAsync(string toltTransactionId)
        {
            RefundCalls++;
            LastRefundTxId = toltTransactionId;
            return Task.FromResult(true);
        }
    }
}
