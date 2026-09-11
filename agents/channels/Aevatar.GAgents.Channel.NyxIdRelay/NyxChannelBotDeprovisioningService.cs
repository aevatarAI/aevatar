using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Result of tearing down the NyxID side of a channel-bot registration.
/// </summary>
/// <param name="ChannelBotRemoved">
/// True when the channel-bot was deleted on NyxID or was already gone (404).
/// </param>
/// <param name="AgentKeyRemoved">
/// True when the Agent Key was deleted on NyxID or was already gone (404).
/// </param>
/// <param name="Succeeded">
/// True only when both hard deletion gates succeeded.
/// </param>
/// <param name="Warnings">
/// Residual best-effort cleanup failures (conversation route / Vault secret).
/// </param>
public sealed record NyxChannelBotDeprovisioningResult(
    bool ChannelBotRemoved,
    bool AgentKeyRemoved,
    bool Succeeded,
    IReadOnlyList<string> Warnings);

public sealed record NyxChannelBotDeprovisioningRequest(
    string RegistrationId,
    string Platform,
    string? ConversationRouteId,
    string? ChannelBotId,
    string? AgentKeyId,
    SecretReference? SecretReference,
    bool AgentKeyDeletionRequired = false)
{
    public static NyxChannelBotDeprovisioningRequest FromRegistration(
        ChannelBotRegistrationEntry registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var authorizationContract = ChannelRegistrationAuthorizationContract.Classify(registration);
        string? agentKeyId;
        SecretReference? secretReference;
        bool agentKeyDeletionRequired;
        switch (authorizationContract)
        {
            case ChannelRegistrationAuthorizationContractKind.NyxIdDefault:
            case ChannelRegistrationAuthorizationContractKind.ExplicitServiceAllowlist:
                agentKeyId = registration.ChannelAgentKey.ApiKeyId;
                secretReference = registration.ChannelAgentKey.SecretReference.Clone();
                agentKeyDeletionRequired = true;
                break;
            case ChannelRegistrationAuthorizationContractKind.HistoricalLegacy:
                agentKeyId = registration.NyxAgentApiKeyId;
                secretReference = registration.WorkflowResultDeliveryCredential?.Clone();
                agentKeyDeletionRequired = !string.IsNullOrWhiteSpace(agentKeyId);
                break;
            default:
                agentKeyId = null;
                secretReference = null;
                agentKeyDeletionRequired = true;
                break;
        }

        return new NyxChannelBotDeprovisioningRequest(
            registration.Id,
            registration.Platform,
            registration.NyxConversationRouteId,
            registration.NyxChannelBotId,
            agentKeyId,
            secretReference,
            AgentKeyDeletionRequired: agentKeyDeletionRequired);
    }
}

/// <summary>
/// Tears down the NyxID resources provisioned for a channel-bot registration (conversation
/// route, channel-bot, Agent Key, and Vault secret) so deleting a registration leaves no orphaned
/// live credential or platform bot. Platform-neutral: it deletes by
/// NyxID id with no per-platform branching, so a single implementation serves both Lark and
/// Telegram registrations.
/// </summary>
public interface INyxChannelBotDeprovisioningService
{
    /// <summary>
    /// Deletes the registration's NyxID resources in reverse of creation order (conversation
    /// route → channel-bot → Agent Key → Vault secret) using the caller's bearer. The route and
    /// Vault operations are best effort; channel-bot and Agent Key deletion are hard gates.
    /// </summary>
    Task<NyxChannelBotDeprovisioningResult> DeprovisionAsync(
        string accessToken,
        NyxChannelBotDeprovisioningRequest request,
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
    private readonly ISecretVault _secretVault;
    private readonly ILogger<NyxChannelBotDeprovisioningService> _logger;

    public NyxChannelBotDeprovisioningService(
        NyxIdApiClient nyxClient,
        ISecretVault secretVault,
        ILogger<NyxChannelBotDeprovisioningService> logger)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxChannelBotDeprovisioningResult> DeprovisionAsync(
        string accessToken,
        NyxChannelBotDeprovisioningRequest request,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(request);

        var warnings = new List<string>();

        // 1. Conversation route (best-effort; reverse of creation order). A residual failure is
        //    a warning, not a blocker — the local tombstone removes the mirror regardless.
        if (!string.IsNullOrWhiteSpace(request.ConversationRouteId))
        {
            var routeRemoved = await TryDeleteAsync(
                () => _nyxClient.DeleteConversationRouteAsync(accessToken, request.ConversationRouteId, ct),
                request.Platform,
                "conversation_route",
                request.ConversationRouteId);
            if (!routeRemoved)
                warnings.Add($"conversation_route_delete_failed id={request.ConversationRouteId}");
        }

        // 2. Channel-bot (authoritative). NyxID enforces one active channel-bot per app_id, so a
        //    residual bot is exactly what blocks re-registration; if this hard-fails (non-404),
        //    the endpoint must NOT tombstone the local mirror so the row stays visible/retryable.
        var channelBotRemoved = true;
        if (!string.IsNullOrWhiteSpace(request.ChannelBotId))
        {
            channelBotRemoved = await TryDeleteAsync(
                () => _nyxClient.DeleteChannelBotAsync(accessToken, request.ChannelBotId, ct),
                request.Platform,
                "channel_bot",
                request.ChannelBotId);
        }

        // 3. Agent Key (hard gate). A live orphaned credential must keep the local registration
        //    visible and retryable, just like a residual channel-bot does.
        var agentKeyDeletionRequired = request.AgentKeyDeletionRequired ||
                                      !string.IsNullOrWhiteSpace(request.AgentKeyId);
        var agentKeyRemoved = !agentKeyDeletionRequired;
        if (!string.IsNullOrWhiteSpace(request.AgentKeyId))
        {
            agentKeyRemoved = await TryDeleteAsync(
                () => _nyxClient.DeleteApiKeyAsync(accessToken, request.AgentKeyId, ct),
                request.Platform,
                "agent_key",
                request.AgentKeyId);
        }

        // 4. Vault revoke (best effort) is legal only after the remote key is gone. Missing or
        //    invalid references do not block the local unregister, but a failed revoke is surfaced.
        if (agentKeyRemoved && request.SecretReference is not null)
            await TryRevokeSecretAsync(request, warnings, ct);

        var succeeded = channelBotRemoved && agentKeyRemoved;
        return new NyxChannelBotDeprovisioningResult(
            ChannelBotRemoved: channelBotRemoved,
            AgentKeyRemoved: agentKeyRemoved,
            Succeeded: succeeded,
            Warnings: warnings);
    }

    private async Task TryRevokeSecretAsync(
        NyxChannelBotDeprovisioningRequest request,
        List<string> warnings,
        CancellationToken ct)
    {
        var reference = request.SecretReference!;
        if (string.IsNullOrWhiteSpace(reference.Ref) ||
            string.IsNullOrWhiteSpace(reference.Purpose) ||
            string.IsNullOrWhiteSpace(reference.OwnerScopeKey) ||
            string.IsNullOrWhiteSpace(request.AgentKeyId))
        {
            warnings.Add("vault_secret_reference_invalid");
            _logger.LogWarning(
                "Channel credential Vault reference was invalid during deprovision: registration={RegistrationId}, agentKeyId={AgentKeyId}",
                request.RegistrationId,
                request.AgentKeyId);
            return;
        }

        try
        {
            var result = await _secretVault.RevokeAsync(
                new RevokeSecretRequest(
                    reference.Ref,
                    reference.Purpose,
                    reference.OwnerScopeKey,
                    request.AgentKeyId,
                    $"channel-registration-delete:{request.RegistrationId}"),
                ct);
            if (!result.Revoked)
            {
                warnings.Add("vault_revoke_failed");
                _logger.LogWarning(
                    "Channel credential Vault revoke did not remove the record during deprovision: registration={RegistrationId}, agentKeyId={AgentKeyId}, failureCode={FailureCode}",
                    request.RegistrationId,
                    request.AgentKeyId,
                    "vault_revoke_failed");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add("vault_revoke_failed");
            _logger.LogWarning(
                "Channel credential Vault revoke failed during deprovision: registration={RegistrationId}, agentKeyId={AgentKeyId}, failureType={FailureType}",
                request.RegistrationId,
                request.AgentKeyId,
                ex.GetType().Name);
        }
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
        string platform,
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
            "NyxID resource deprovision failed: type={ResourceType}, id={ResourceId}, status={Status}",
            resourceType,
            resourceId,
            TryGetErrorStatus(response, out var failureStatus) ? failureStatus : 0);
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
