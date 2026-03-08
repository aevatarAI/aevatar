using Aevatar.App.Application.Projection.Orchestration;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.App.Application.Services;

public sealed class RevenueCatWebhookHandler : IRevenueCatWebhookHandler
{
    private static readonly HashSet<string> PaymentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "INITIAL_PURCHASE", "RENEWAL", "NON_RENEWING_PURCHASE"
    };

    private readonly IActorAccessAppService _actors;
    private readonly IAppProjectionManager _projectionManager;
    private readonly IToltAppService _toltService;
    private readonly IProjectionDocumentStore<AppUserAffiliateReadModel, string> _affiliateStore;
    private readonly IProjectionDocumentStore<AppPaymentTransactionReadModel, string> _transactionStore;
    private readonly ILogger<RevenueCatWebhookHandler> _logger;

    public RevenueCatWebhookHandler(
        IActorAccessAppService actors,
        IAppProjectionManager projectionManager,
        IToltAppService toltService,
        IProjectionDocumentStore<AppUserAffiliateReadModel, string> affiliateStore,
        IProjectionDocumentStore<AppPaymentTransactionReadModel, string> transactionStore,
        ILogger<RevenueCatWebhookHandler> logger)
    {
        _actors = actors;
        _projectionManager = projectionManager;
        _toltService = toltService;
        _affiliateStore = affiliateStore;
        _transactionStore = transactionStore;
        _logger = logger;
    }

    public async Task HandleAsync(RevenueCatWebhookPayload payload)
    {
        var evt = payload.Event;
        if (evt?.Type is null || evt.TransactionId is null || evt.AppUserId is null)
        {
            _logger.LogWarning("RevenueCat webhook missing required fields");
            return;
        }

        if (PaymentTypes.Contains(evt.Type))
        {
            await HandlePaymentAsync(evt);
        }
        else if (string.Equals(evt.Type, "CANCELLATION", StringComparison.OrdinalIgnoreCase)
                 && string.Equals(evt.CancelReason, "CUSTOMER_SUPPORT", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRefundAsync(evt);
        }
    }

    private async Task HandlePaymentAsync(RevenueCatEvent evt)
    {
        var amount = (int)(evt.PriceInPurchasedCurrency * 100);
        var amountUsd = (int)(evt.Price * 100);
        var billingType = string.Equals(evt.Type, "NON_RENEWING_PURCHASE", StringComparison.OrdinalIgnoreCase)
            ? "one_time" : "subscription";

        await _projectionManager.EnsureSubscribedAsync(
            _actors.ResolveActorId<PaymentTransactionGAgent>(evt.TransactionId!));

        await _actors.SendCommandAsync<PaymentTransactionGAgent>(evt.TransactionId!,
            new PaymentTransactionCreatedEvent
            {
                UserId = evt.AppUserId!,
                TransactionId = evt.TransactionId!,
                OriginalTransactionId = evt.OriginalTransactionId ?? "",
                Amount = amount,
                Currency = evt.Currency ?? "",
                AmountUsd = amountUsd,
                BillingType = billingType,
                ProductId = evt.ProductId ?? "",
                Store = evt.Store ?? "",
                CreatedAt = Timestamp.FromDateTimeOffset(
                    evt.PurchasedAtMs is > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(evt.PurchasedAtMs.Value)
                        : DateTimeOffset.UtcNow)
            });

        // Affiliate is created during the earlier bind phase (POST /api/referral/bind),
        // so the read model is already projected by the time a payment webhook arrives.
        var affiliateActorId = _actors.ResolveActorId<UserAffiliateGAgent>(evt.AppUserId!);
        var affiliate = await _affiliateStore.GetAsync(affiliateActorId);
        if (affiliate?.CustomerId is not { Length: > 0 } customerId)
            return;

        var result = await _toltService.TrackPaymentAsync(
            customerId, amountUsd, billingType,
            evt.TransactionId!, evt.ProductId ?? "", (evt.Store ?? "").ToLowerInvariant());

        if (result is { Success: true, ToltTransactionId: { Length: > 0 } affiliateTxId })
        {
            await _actors.SendCommandAsync<PaymentTransactionGAgent>(evt.TransactionId!,
                new PaymentTransactionAffiliateTrackedEvent
                {
                    TransactionId = evt.TransactionId!,
                    AffiliateTransactionId = affiliateTxId,
                    AffiliatePlatform = affiliate.Platform
                });
        }
    }

    private async Task HandleRefundAsync(RevenueCatEvent evt)
    {
        await _projectionManager.EnsureSubscribedAsync(
            _actors.ResolveActorId<PaymentTransactionGAgent>(evt.TransactionId!));

        await _actors.SendCommandAsync<PaymentTransactionGAgent>(evt.TransactionId!,
            new PaymentTransactionRefundedEvent
            {
                TransactionId = evt.TransactionId!,
                RefundedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
            });

        // AffiliateTransactionId was written during the earlier payment phase,
        // so the read model is already projected before a refund webhook arrives.
        var txActorId = _actors.ResolveActorId<PaymentTransactionGAgent>(evt.TransactionId!);
        var txRecord = await _transactionStore.GetAsync(txActorId);
        if (txRecord?.AffiliateTransactionId is { Length: > 0 } affiliateTxId)
        {
            await _toltService.TrackRefundAsync(affiliateTxId);
        }
    }
}
