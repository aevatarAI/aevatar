using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>Cleanup result for the registration-owned Agent Key, route and Vault reference.</summary>
public sealed record NyxChannelBotDeprovisioningResult(
    bool AgentKeyRemoved,
    bool Succeeded,
    IReadOnlyList<string> Warnings,
    bool ConversationRouteRemoved = true,
    bool VaultSecretRevoked = true,
    NyxChannelBotDeprovisioningRequest? RetryRequest = null)
{
    // Succeeded retains the unregister contract: remote route/key removal permits tombstoning.
    // Adoption compensation must additionally retain the Vault handle until revoke is confirmed.
    public bool CleanupComplete => Succeeded && VaultSecretRevoked;
}

/// <summary>Known ownership for a route POST whose response did not establish its outcome.</summary>
public sealed record NyxChannelRouteAcquisitionUncertainty(string ChannelBotId, string? OrganizationId);

public sealed record NyxChannelBotDeprovisioningRequest(
    string RegistrationId,
    string Platform,
    string? ConversationRouteId,
    string? AgentKeyId,
    SecretReference? SecretReference,
    bool AgentKeyDeletionRequired = false,
    NyxChannelRouteAcquisitionUncertainty? UncertainRouteAcquisition = null)
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
            agentKeyId,
            secretReference,
            AgentKeyDeletionRequired: agentKeyDeletionRequired);
    }
}

/// <summary>Cleans registration-owned resources, preserving adopted Bots and existing UserServices.</summary>
public interface INyxChannelBotDeprovisioningService
{
    Task<NyxChannelBotDeprovisioningResult> DeprovisionAsync(
        string accessToken,
        NyxChannelBotDeprovisioningRequest request,
        CancellationToken ct);
}

public sealed class NyxChannelBotDeprovisioningService : INyxChannelBotDeprovisioningService
{
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

        if (request.UncertainRouteAcquisition is not null)
        {
            // A missing response ID is not proof of absence. Keep the newly acquired Key and
            // Vault handle until the route can be attributed to this request's Bot and Key.
            var routeId = await ResolveAcquiredRouteAsync(accessToken, request, ct);
            if (routeId is null)
                return new(false, false, ["conversation_route_acquisition_unresolved"],
                    ConversationRouteRemoved: false, VaultSecretRevoked: false, RetryRequest: request);

            var resolvedRequest = request with
            {
                ConversationRouteId = routeId,
                UncertainRouteAcquisition = null,
            };
            try
            {
                return await DeprovisionAsync(accessToken, resolvedRequest, ct);
            }
            catch (Exception ex)
            {
                // Preserve the resolved ID even if cancellation interrupts later deletion.
                // Re-listing after a successful route DELETE cannot recover that ID again.
                _logger.LogWarning("Resolved route cleanup interrupted: registration={RegistrationId}, failureType={FailureType}",
                    request.RegistrationId, ex.GetType().Name);
                return new(false, false, ["owned_cleanup_interrupted"],
                    ConversationRouteRemoved: false, VaultSecretRevoked: false, RetryRequest: resolvedRequest);
            }
        }

        var warnings = new List<string>();

        // 1. Conversation route (reverse of creation order). A failed delete keeps the local
        //    registration visible and retryable because the route is still an owned live handle.
        var conversationRouteRemoved = true;
        if (!string.IsNullOrWhiteSpace(request.ConversationRouteId))
        {
            conversationRouteRemoved = await TryDeleteAsync(
                () => _nyxClient.DeleteConversationRouteAsync(accessToken, request.ConversationRouteId, ct),
                request.Platform,
                "conversation_route",
                request.ConversationRouteId);
            if (!conversationRouteRemoved)
                warnings.Add($"conversation_route_delete_failed id={request.ConversationRouteId}");
        }

        // A live registration-owned Agent Key keeps the local row visible for retry.
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

        // 3. Vault revoke (best effort) is legal only after the remote key is gone. Missing or
        //    invalid references do not block the local unregister, but a failed revoke is surfaced.
        var vaultSecretRevoked = request.SecretReference is null;
        if (agentKeyRemoved && request.SecretReference is not null)
            vaultSecretRevoked = await TryRevokeSecretAsync(request, warnings, ct);

        // The registration may only be tombstoned after every owned remote handle is gone. A
        // route failure is therefore an incomplete cleanup even when key deletion succeeded;
        // callers retain the registration and its stable IDs for a retry.
        var succeeded = conversationRouteRemoved && agentKeyRemoved;
        return new NyxChannelBotDeprovisioningResult(
            AgentKeyRemoved: agentKeyRemoved,
            Succeeded: succeeded,
            Warnings: warnings,
            ConversationRouteRemoved: conversationRouteRemoved,
            VaultSecretRevoked: vaultSecretRevoked,
            RetryRequest: succeeded && vaultSecretRevoked ? null : request);
    }

    private async Task<string?> ResolveAcquiredRouteAsync(
        string accessToken,
        NyxChannelBotDeprovisioningRequest request,
        CancellationToken ct)
    {
        var ownership = request.UncertainRouteAcquisition!;
        if (!NyxChannelBotIdentity.IsValid(ownership.ChannelBotId) ||
            !NyxChannelBotIdentity.IsValid(request.AgentKeyId))
            return null;

        try
        {
            var response = await _nyxClient.ListConversationRoutesAsync(
                accessToken, ownership.ChannelBotId, ownership.OrganizationId, ct);
            if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
                return null;
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("conversations", out var routes) || routes.ValueKind != JsonValueKind.Array)
                return null;

            string? ownedRouteId = null;
            foreach (var route in routes.EnumerateArray())
            {
                if (route.ValueKind != JsonValueKind.Object ||
                    route.EnumerateObject().GroupBy(static p => p.Name, StringComparer.Ordinal).Any(static g => g.Count() != 1) ||
                    !TryReadIdentity(route, "id", out var routeId) ||
                    !TryReadIdentity(route, "channel_bot_id", out var botId) ||
                    !TryReadIdentity(route, "agent_api_key_id", out var keyId))
                    return null;
                if (!string.Equals(botId, ownership.ChannelBotId, StringComparison.Ordinal) ||
                    !string.Equals(keyId, request.AgentKeyId, StringComparison.Ordinal))
                    continue;
                if (ownedRouteId is not null)
                    return null;
                ownedRouteId = routeId;
            }
            // An empty/eventually visible list is not evidence that the POST never committed.
            return ownedRouteId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Route acquisition reconciliation failed: registration={RegistrationId}, failureType={FailureType}",
                request.RegistrationId, ex.GetType().Name);
            return null;
        }
    }

    private static bool TryReadIdentity(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return NyxChannelBotIdentity.IsValid(value);
    }

    private async Task<bool> TryRevokeSecretAsync(
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
            return false;
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
            return result.Revoked;
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
            return false;
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
        string response;
        try
        {
            response = await delete();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "NyxID resource deprovision threw: type={ResourceType}, id={ResourceId}, failureType={FailureType}",
                resourceType,
                resourceId,
                ex.GetType().Name);
            return false;
        }

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
