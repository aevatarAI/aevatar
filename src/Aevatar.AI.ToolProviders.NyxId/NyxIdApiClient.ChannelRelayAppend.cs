using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId;

public enum NyxIdChannelRelayAppendState
{
    PreDispatchFailure,
    Accepted,
    Rejected,
    DeliveryUnknown,
}

/// <summary>Sanitized boundary result for a single non-replayable Relay append request.</summary>
public sealed record NyxIdChannelRelayAppendResult(
    NyxIdChannelRelayAppendState State,
    string PlatformMessageId = "",
    string ErrorCode = "",
    int HttpStatus = 0);

public sealed partial class NyxIdApiClient
{
    /// <summary>
    /// Sends one fixed append segment without public-transport fallback or application retries.
    /// The dispatch callback starts on the caller's scheduler after local preparation;
    /// its commit must complete before HttpClient.SendAsync.
    /// </summary>
    public async Task<NyxIdChannelRelayAppendResult> SendChannelRelayAppendTextAsync(
        string agentKey,
        string messageId,
        string preparedText,
        Func<CancellationToken, Task> onDispatch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onDispatch);
        if (string.IsNullOrWhiteSpace(agentKey) || string.IsNullOrWhiteSpace(messageId) ||
            string.IsNullOrWhiteSpace(preparedText))
        {
            return new(NyxIdChannelRelayAppendState.PreDispatchFailure, ErrorCode: "append_request_invalid");
        }

        var dispatchFenceEntered = false;
        var stage = ChannelRelayAppendRequestStage.Preparation;
        var httpStatus = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetPublicApiBaseUrl()}/api/v1/channel-relay/reply");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agentKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                message_id = messageId,
                reply = new { text = preparedText },
            }), Encoding.UTF8, "application/json");
            ct.ThrowIfCancellationRequested();

            // A failed commit callback may still have committed its durable fence. Never reopen
            // token fallback or issue HTTP when that callback cannot confirm completion.
            dispatchFenceEntered = true;
            stage = ChannelRelayAppendRequestStage.DispatchFence;
            await onDispatch(ct).ConfigureAwait(false);
            stage = ChannelRelayAppendRequestStage.HttpSend;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                // Authentication/authorization refusals reject this caller before a platform
                // send. Other statuses do not identify the send stage and remain uncertain.
                var rejected = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                return new(rejected ? NyxIdChannelRelayAppendState.Rejected : NyxIdChannelRelayAppendState.DeliveryUnknown,
                    ErrorCode: rejected ? "append_http_rejected" : "append_http_outcome_unknown", HttpStatus: httpStatus);
            }

            stage = ChannelRelayAppendRequestStage.ResponseBody;
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) ||
                !TryGetAppendMessageId(root, "message_id", agentKey, out _))
            {
                return new(NyxIdChannelRelayAppendState.DeliveryUnknown,
                    ErrorCode: "append_response_invalid", HttpStatus: httpStatus);
            }

            var platformMessageId = "";
            if (!TryGetOptionalAppendMessageId(root, "platform_message_id", agentKey, out platformMessageId))
            {
                return new(NyxIdChannelRelayAppendState.DeliveryUnknown,
                    ErrorCode: "append_response_invalid", HttpStatus: httpStatus);
            }
            if (string.IsNullOrEmpty(platformMessageId) &&
                !TryGetOptionalAppendMessageId(root, "upstream_message_id", agentKey, out platformMessageId))
            {
                return new(NyxIdChannelRelayAppendState.DeliveryUnknown,
                    ErrorCode: "append_response_invalid", HttpStatus: httpStatus);
            }
            return new(NyxIdChannelRelayAppendState.Accepted, platformMessageId, HttpStatus: httpStatus);
        }
        catch (Exception error)
        {
            // Neither exception messages nor response bodies cross this boundary: either may
            // contain an echoed credential or the submitted text.
            _logger.LogWarning(
                "Nyx channel relay append failed: stage={Stage} exceptionType={ExceptionType} causeType={CauseType} httpStatus={HttpStatus}",
                stage, error.GetType().Name, error.GetBaseException().GetType().Name, httpStatus);
            var errorCode = stage == ChannelRelayAppendRequestStage.DispatchFence
                ? "append_dispatch_fence_failed"
                : dispatchFenceEntered ? "append_outcome_unknown" : "append_request_unavailable";
            return new(dispatchFenceEntered ? NyxIdChannelRelayAppendState.DeliveryUnknown : NyxIdChannelRelayAppendState.PreDispatchFailure,
                ErrorCode: errorCode, HttpStatus: httpStatus);
        }
    }

    private enum ChannelRelayAppendRequestStage
    {
        Preparation,
        DispatchFence,
        HttpSend,
        ResponseBody,
    }

    private static bool TryGetOptionalAppendMessageId(JsonElement root, string field, string agentKey, out string value)
    {
        value = "";
        if (!root.TryGetProperty(field, out var property) || property.ValueKind == JsonValueKind.Null ||
            (property.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(property.GetString())))
            return true;

        // Telegram already exposes numeric upstream IDs through the ordinary Relay reply
        // contract. Keep that boundary shape without relaxing the required NyxID row ID.
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numericId))
        {
            value = numericId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return TryGetAppendMessageId(root, field, agentKey, out value);
    }

    private static bool TryGetAppendMessageId(JsonElement root, string field, string agentKey, out string value)
    {
        value = "";
        if (!root.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 512 || candidate.Any(char.IsWhiteSpace) ||
            candidate.Any(char.IsControl) || candidate.Contains(agentKey, StringComparison.Ordinal))
            return false;

        value = candidate;
        return true;
    }
}
