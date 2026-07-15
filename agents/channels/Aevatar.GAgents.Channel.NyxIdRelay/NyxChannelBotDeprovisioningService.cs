using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Result of tearing down the NyxID side of a channel-bot registration.
/// </summary>
/// <param name="ChannelBotRemoved">
/// True when the channel-bot was deleted on NyxID OR was already gone (404). This is the
/// authoritative signal for whether the local mirror tombstone may proceed.
/// </param>
/// <param name="Succeeded">
/// Alias for <paramref name="ChannelBotRemoved"/>; the gate the endpoint reads. A hard
/// (non-404) channel-bot delete failure makes this false so the caller can retry and the
/// local mirror is left intact.
/// </param>
/// <param name="Warnings">
/// Residual best-effort cleanup failures (conversation route / relay api-key). These never
/// block the local tombstone once the channel-bot is gone; they are surfaced so an orphaned
/// route/api-key is honestly reported rather than hidden.
/// </param>
public sealed record NyxChannelBotDeprovisioningResult(
    bool ChannelBotRemoved,
    bool Succeeded,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Tears down the NyxID resources provisioned for a channel-bot registration (conversation
/// route, channel-bot, relay api-key) so deleting a registration leaves no orphaned NyxID
/// resources and the same app can be re-registered cleanly. Platform-neutral: it deletes by
/// NyxID id with no per-platform branching, so a single implementation serves both Lark and
/// Telegram registrations.
/// </summary>
public interface INyxChannelBotDeprovisioningService
{
    /// <summary>
    /// Deletes the registration's NyxID resources in reverse of creation order (conversation
    /// route → channel-bot → relay api-key) using the caller's bearer. A NyxID 404 / not-found
    /// on any resource counts as success (idempotent). The channel-bot delete is authoritative;
    /// route + api-key deletes are best-effort and only contribute warnings.
    /// </summary>
    Task<NyxChannelBotDeprovisioningResult> DeprovisionAsync(
        string accessToken,
        string? conversationRouteId,
        string? channelBotId,
        string? apiKeyId,
        CancellationToken ct);
}

public sealed class NyxChannelBotDeprovisioningService : INyxChannelBotDeprovisioningService
{
    // Deprovision is the reverse of the register-side provisioning saga
    // (NyxLarkProvisioningService / NyxTelegramProvisioningService): register provisions on
    // NyxID then writes the local mirror; delete tears down NyxID then tombstones the mirror.
    // No NyxID call lives in the registration actor — the actor stays a pure local fact owner;
    // the endpoint orchestrates NyxID teardown before the local unregister command, exactly as
    // the register endpoint orchestrates provisioning before the local mirror write.
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<NyxChannelBotDeprovisioningService> _logger;

    public NyxChannelBotDeprovisioningService(
        NyxIdApiClient nyxClient,
        ILogger<NyxChannelBotDeprovisioningService> logger)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxChannelBotDeprovisioningResult> DeprovisionAsync(
        string accessToken,
        string? conversationRouteId,
        string? channelBotId,
        string? apiKeyId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var warnings = new List<string>();

        // 1. Conversation route (best-effort; reverse of creation order). A residual failure is
        //    a warning, not a blocker — the local tombstone removes the mirror regardless.
        if (!string.IsNullOrWhiteSpace(conversationRouteId))
        {
            var routeRemoved = await TryDeleteAsync(
                () => _nyxClient.DeleteConversationRouteAsync(accessToken, conversationRouteId, ct),
                "conversation_route",
                conversationRouteId);
            if (!routeRemoved)
                warnings.Add($"conversation_route_delete_failed id={conversationRouteId}");
        }

        // 2. Channel-bot (authoritative). NyxID enforces one active channel-bot per app_id, so a
        //    residual bot is exactly what blocks re-registration; if this hard-fails (non-404),
        //    the endpoint must NOT tombstone the local mirror so the row stays visible/retryable.
        var channelBotRemoved = true;
        if (!string.IsNullOrWhiteSpace(channelBotId))
        {
            channelBotRemoved = await TryDeleteAsync(
                () => _nyxClient.DeleteChannelBotAsync(accessToken, channelBotId, ct),
                "channel_bot",
                channelBotId);
        }

        // 3. Relay api-key (best-effort). Only attempt once the channel-bot is gone — a stuck
        //    bot means we are aborting the teardown and leaving the api-key it may still use.
        if (channelBotRemoved && !string.IsNullOrWhiteSpace(apiKeyId))
        {
            var apiKeyRemoved = await TryDeleteAsync(
                () => _nyxClient.DeleteApiKeyAsync(accessToken, apiKeyId, ct),
                "relay_api_key",
                apiKeyId);
            if (!apiKeyRemoved)
                warnings.Add($"relay_api_key_delete_failed id={apiKeyId}");
        }

        return new NyxChannelBotDeprovisioningResult(
            ChannelBotRemoved: channelBotRemoved,
            Succeeded: channelBotRemoved,
            Warnings: warnings);
    }

    /// <summary>
    /// Invokes a NyxID delete and classifies the result. Successful NyxID deletes return
    /// <c>204 No Content</c>, which <see cref="NyxIdApiClient.DeleteAsync"/> represents as an empty
    /// string. Non-2xx responses become <c>{"error":true,"status":...}</c> envelopes, so success
    /// is "empty response", "no error envelope", or "error envelope whose status is 404"
    /// (already gone / idempotent re-delete). <see cref="OperationCanceledException"/> is a
    /// control-flow signal and is allowed to propagate.
    /// </summary>
    private async Task<bool> TryDeleteAsync(
        Func<Task<string>> delete,
        string resourceType,
        string resourceId)
    {
        var response = await delete();

        if (string.IsNullOrWhiteSpace(response) ||
            !NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            return true;

        if (TryGetErrorStatus(response, out var status) && status == 404)
        {
            _logger.LogInformation(
                "NyxID resource already absent during deprovision (treated as success): type={ResourceType}, id={ResourceId}",
                resourceType,
                resourceId);
            return true;
        }

        _logger.LogWarning(
            "NyxID resource deprovision failed: type={ResourceType}, id={ResourceId}, detail={Detail}",
            resourceType,
            resourceId,
            NyxApiResponseHelper.ExtractErrorDetail(response));
        return false;
    }

    private static bool TryGetErrorStatus(string response, out int status)
    {
        status = 0;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var statusElement) ||
                statusElement.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            status = statusElement.GetInt32();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
