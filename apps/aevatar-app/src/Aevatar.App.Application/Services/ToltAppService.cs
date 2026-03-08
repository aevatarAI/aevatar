using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Services;

public sealed class ToltAppService : IToltAppService
{
    private readonly HttpClient _httpClient;
    private readonly ToltOptions _options;
    private readonly ILogger<ToltAppService> _logger;

    public ToltAppService(
        HttpClient httpClient,
        IOptions<ToltOptions> options,
        ILogger<ToltAppService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ToltClickResult?> TrackClickAsync(
        string refValue, string pageUrl, string? device)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_options.BaseUrl}/v1/clicks",
                new ToltClickRequest("ref", refValue, pageUrl, device ?? ""));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tolt click tracking failed: {Status}", response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<ToltClickResponse>();
            var partnerId = result?.Data?.FirstOrDefault()?.PartnerId;
            return partnerId is { Length: > 0 }
                ? new ToltClickResult(partnerId)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tolt click tracking error for ref={Ref}", refValue);
            return null;
        }
    }

    public async Task<ToltBindResult> BindReferralAsync(
        string email, string referralCode, string userId)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_options.BaseUrl}/v1/customers",
                new ToltCustomerRequest(email, referralCode, userId));

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "Tolt customer creation failed: {Status} {Body}",
                    response.StatusCode, body);
                return new ToltBindResult(false, null, $"Tolt API returned {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<ToltCustomerResponse>();
            var customerId = result?.Data?.FirstOrDefault()?.Id;
            return new ToltBindResult(true, customerId, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tolt customer creation error for {Email}", email);
            return new ToltBindResult(false, null, "Internal error");
        }
    }

    public async Task<ToltPaymentResult> TrackPaymentAsync(
        string customerId, int amountUsd, string billingType,
        string transactionId, string productId, string source)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_options.BaseUrl}/v1/transactions",
                new ToltTransactionRequest(
                    customerId, amountUsd, billingType,
                    transactionId, productId, source));

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "Tolt payment tracking failed: {Status} {Body}",
                    response.StatusCode, body);
                return new ToltPaymentResult(false, null, $"Tolt API returned {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<ToltTransactionResponse>();
            var txnId = result?.Data?.FirstOrDefault()?.Id;
            return new ToltPaymentResult(true, txnId, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tolt payment tracking error for customer={CustomerId}", customerId);
            return new ToltPaymentResult(false, null, "Internal error");
        }
    }

    public async Task<bool> TrackRefundAsync(string toltTransactionId)
    {
        try
        {
            var response = await _httpClient.PutAsync(
                $"{_options.BaseUrl}/v1/transactions/{toltTransactionId}/refund",
                content: null);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "Tolt refund tracking failed: {Status} {Body}",
                    response.StatusCode, body);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<ToltTransactionResponse>();
            var status = result?.Data?.FirstOrDefault()?.Status;
            if (!string.Equals(status, "refunded", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Tolt refund status={Status} for txId={TxId}", status, toltTransactionId);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tolt refund tracking error for txId={TxId}", toltTransactionId);
            return false;
        }
    }

    private sealed record ToltClickRequest(
        [property: JsonPropertyName("param")] string Param,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("page")] string Page,
        [property: JsonPropertyName("device")] string Device);

    private sealed record ToltClickResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] ToltClickData[]? Data);

    private sealed record ToltClickData(
        [property: JsonPropertyName("partner_id")] string? PartnerId);

    private sealed record ToltCustomerRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("partner_id")] string PartnerId,
        [property: JsonPropertyName("customer_id")] string CustomerId);

    private sealed record ToltCustomerResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] ToltCustomerData[]? Data);

    private sealed record ToltCustomerData(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("customer_id")] string? CustomerId);

    private sealed record ToltTransactionRequest(
        [property: JsonPropertyName("customer_id")] string CustomerId,
        [property: JsonPropertyName("amount")] int Amount,
        [property: JsonPropertyName("billing_type")] string BillingType,
        [property: JsonPropertyName("charge_id")] string ChargeId,
        [property: JsonPropertyName("product_id")] string ProductId,
        [property: JsonPropertyName("source")] string Source);

    private sealed record ToltTransactionResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] ToltTransactionData[]? Data);

    private sealed record ToltTransactionData(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] string? Status);
}
