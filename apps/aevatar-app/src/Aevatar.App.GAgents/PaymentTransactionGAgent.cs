using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents;

public sealed class PaymentTransactionGAgent
    : GAgentBase<PaymentTransactionState>
{
    [EventHandler]
    public async Task HandleCreateTransaction(PaymentTransactionCreatedEvent evt)
    {
        if (State.CreatedAt is not null)
            return;

        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleTrackAffiliate(PaymentTransactionAffiliateTrackedEvent evt)
    {
        if (State.CreatedAt is null || State.AffiliateTransactionId is { Length: > 0 })
            return;

        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleRefundTransaction(PaymentTransactionRefundedEvent evt)
    {
        if (State.CreatedAt is null || State.Refunded)
            return;

        await PersistDomainEventAsync(evt);
    }

    protected override PaymentTransactionState TransitionState(
        PaymentTransactionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<PaymentTransactionCreatedEvent>((s, e) =>
            {
                s.TransactionId = e.TransactionId;
                s.UserId = e.UserId;
                s.OriginalTransactionId = e.OriginalTransactionId;
                s.Amount = e.Amount;
                s.Currency = e.Currency;
                s.AmountUsd = e.AmountUsd;
                s.BillingType = e.BillingType;
                s.ProductId = e.ProductId;
                s.Store = e.Store;
                s.CreatedAt = e.CreatedAt;
                return s;
            })
            .On<PaymentTransactionAffiliateTrackedEvent>((s, e) =>
            {
                s.AffiliateTransactionId = e.AffiliateTransactionId;
                s.AffiliatePlatform = e.AffiliatePlatform;
                return s;
            })
            .On<PaymentTransactionRefundedEvent>((s, e) =>
            {
                s.Refunded = true;
                s.RefundedAt = e.RefundedAt;
                return s;
            })
            .OrCurrent();
}
