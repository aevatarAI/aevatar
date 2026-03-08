using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.App.Application.Projection.Reducers;

public sealed class PaymentTransactionCreatedEventReducer
    : AppEventReducerBase<AppPaymentTransactionReadModel, PaymentTransactionCreatedEvent>
{
    protected override bool Reduce(
        AppPaymentTransactionReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        PaymentTransactionCreatedEvent evt,
        DateTimeOffset now)
    {
        if (readModel.CreatedAt != default)
            return false;

        readModel.TransactionId = evt.TransactionId;
        readModel.UserId = evt.UserId;
        readModel.OriginalTransactionId = evt.OriginalTransactionId;
        readModel.Amount = evt.Amount;
        readModel.Currency = evt.Currency;
        readModel.AmountUsd = evt.AmountUsd;
        readModel.BillingType = evt.BillingType;
        readModel.ProductId = evt.ProductId;
        readModel.Store = evt.Store;
        readModel.CreatedAt = evt.CreatedAt?.ToDateTimeOffset() ?? now;
        return true;
    }
}

public sealed class PaymentTransactionAffiliateTrackedEventReducer
    : AppEventReducerBase<AppPaymentTransactionReadModel, PaymentTransactionAffiliateTrackedEvent>
{
    protected override bool Reduce(
        AppPaymentTransactionReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        PaymentTransactionAffiliateTrackedEvent evt,
        DateTimeOffset now)
    {
        if (readModel.AffiliateTransactionId is { Length: > 0 })
            return false;

        readModel.AffiliateTransactionId = evt.AffiliateTransactionId;
        readModel.AffiliatePlatform = evt.AffiliatePlatform;
        return true;
    }
}

public sealed class PaymentTransactionRefundedEventReducer
    : AppEventReducerBase<AppPaymentTransactionReadModel, PaymentTransactionRefundedEvent>
{
    protected override bool Reduce(
        AppPaymentTransactionReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        PaymentTransactionRefundedEvent evt,
        DateTimeOffset now)
    {
        if (readModel.Refunded)
            return false;

        readModel.Refunded = true;
        readModel.RefundedAt = evt.RefundedAt?.ToDateTimeOffset() ?? now;
        return true;
    }
}
