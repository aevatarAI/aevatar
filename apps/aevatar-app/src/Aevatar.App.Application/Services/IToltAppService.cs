namespace Aevatar.App.Application.Services;

public interface IToltAppService
{
    Task<ToltClickResult?> TrackClickAsync(string refValue, string pageUrl, string? device);

    Task<ToltBindResult> BindReferralAsync(string email, string referralCode, string userId);

    Task<ToltPaymentResult> TrackPaymentAsync(string customerId, int amountUsd,
        string billingType, string transactionId, string productId, string source);

    Task<bool> TrackRefundAsync(string toltTransactionId);
}

public sealed record ToltClickResult(string PartnerId);

public sealed record ToltBindResult(bool Success, string? CustomerId, string? Error);

public sealed record ToltPaymentResult(bool Success, string? ToltTransactionId, string? Error);
