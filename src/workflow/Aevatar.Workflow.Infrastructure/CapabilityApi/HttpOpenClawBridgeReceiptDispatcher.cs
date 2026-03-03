using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.OpenClaw;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class HttpOpenClawBridgeReceiptDispatcher : IOpenClawBridgeReceiptDispatcher
{
    public const string ReceiptClientName = "openclaw.bridge.receipt";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<HttpOpenClawBridgeReceiptDispatcher> _logger;

    public HttpOpenClawBridgeReceiptDispatcher(
        ILogger<HttpOpenClawBridgeReceiptDispatcher> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task DispatchAsync(
        OpenClawBridgeReceiptDispatchRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CallbackUrl))
            return;
        if (!Uri.TryCreate(request.CallbackUrl, UriKind.Absolute, out var callbackUri))
            return;

        var bodyJson = JsonSerializer.Serialize(
            new
            {
                eventId = request.EventId,
                sequence = request.Sequence,
                type = request.EventType,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                correlationId = request.CorrelationId,
                idempotencyKey = request.IdempotencyKey,
                sessionKey = request.SessionKey,
                channelId = request.ChannelId,
                userId = request.UserId,
                messageId = request.MessageId,
                actorId = request.ActorId,
                commandId = request.CommandId,
                workflowName = request.WorkflowName,
                metadata = request.Metadata,
                payload = request.Payload,
            });

        var timeoutMs = Math.Clamp(request.TimeoutMs, 500, 60_000);
        var maxAttempts = Math.Clamp(request.MaxAttempts, 1, 5);
        var retryDelayMs = Math.Clamp(request.RetryDelayMs, 100, 10_000);
        var authToken = string.IsNullOrWhiteSpace(request.CallbackToken) ? string.Empty : request.CallbackToken;

        var adHocClient = _httpClientFactory == null ? new HttpClient() : null;
        var client = _httpClientFactory?.CreateClient(ReceiptClientName) ?? adHocClient!;
        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, callbackUri);
                if (!string.IsNullOrWhiteSpace(authToken))
                {
                    if (string.Equals(request.AuthHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        requestMessage.Headers.TryAddWithoutValidation(
                            request.AuthHeaderName,
                            $"{request.AuthScheme} {authToken}".Trim());
                    }
                    else
                    {
                        requestMessage.Headers.TryAddWithoutValidation(request.AuthHeaderName, authToken);
                    }
                }

                requestMessage.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

                try
                {
                    using var response = await client.SendAsync(requestMessage, timeoutCts.Token);
                    if (response.IsSuccessStatusCode)
                        return;

                    _logger.LogWarning(
                        "OpenClaw bridge callback returned non-success status. status={StatusCode} event={EventType} attempt={Attempt}/{MaxAttempts}",
                        (int)response.StatusCode,
                        request.EventType,
                        attempt,
                        maxAttempts);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "OpenClaw bridge callback delivery failed. event={EventType} attempt={Attempt}/{MaxAttempts}",
                        request.EventType,
                        attempt,
                        maxAttempts);
                }

                if (attempt < maxAttempts)
                    await Task.Delay(retryDelayMs, CancellationToken.None);
            }
        }
        finally
        {
            adHocClient?.Dispose();
        }
    }
}
