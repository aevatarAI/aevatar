using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.ReadModels;

public sealed class AppPaymentTransactionReadModel : IProjectionReadModel
{
    public string Id { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string OriginalTransactionId { get; set; } = "";
    public int Amount { get; set; }
    public string Currency { get; set; } = "";
    public int AmountUsd { get; set; }
    public string BillingType { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Store { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string AffiliateTransactionId { get; set; } = "";
    public string AffiliatePlatform { get; set; } = "";
    public bool Refunded { get; set; }
    public DateTimeOffset? RefundedAt { get; set; }
}
