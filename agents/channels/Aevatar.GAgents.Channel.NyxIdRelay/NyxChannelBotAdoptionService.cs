using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationExplicitAuthorization;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

internal static class NyxChannelBotIdentity
{
    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) && !value.Any(char.IsControl);
}

/// <summary>A caller-authorized, owner-aligned, adoptable NyxID Bot detail.</summary>
public sealed class VerifiedNyxChannelBotDetail
{
    private VerifiedNyxChannelBotDetail(string id, ChannelPlatformId platform, string webhookUrl,
        VerifiedChannelRegistrationOwner owner)
    {
        Id = id;
        Platform = platform;
        WebhookUrl = webhookUrl;
        Owner = owner;
    }

    public string Id { get; }
    public ChannelPlatformId Platform { get; }
    public string WebhookUrl { get; }
    public VerifiedChannelRegistrationOwner Owner { get; }

    public sealed record ReadResult(VerifiedNyxChannelBotDetail? Bot, string ErrorCode)
    {
        public bool Succeeded => Bot is not null && ErrorCode.Length == 0;
    }

    /// <summary>The third-party boundary is the only place that can mint verified Bot details.</summary>
    public sealed class Reader(NyxIdApiClient client)
    {
        public async Task<ReadResult> ReadAsync(string accessToken, string botId, string? assertedPlatform,
            VerifiedChannelRegistrationOwner owner, CancellationToken ct)
        {
            if (!NyxChannelBotIdentity.IsValid(botId))
                return new(null, "missing_nyx_channel_bot_id");
            try
            {
                var response = await client.GetChannelBotAsync(accessToken, botId, ct);
                using var document = JsonDocument.Parse(response);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
                    return new(null, "channel_bot_not_found_or_forbidden");
                if (root.ValueKind != JsonValueKind.Object ||
                    root.EnumerateObject().GroupBy(static p => p.Name, StringComparer.Ordinal).Any(static g => g.Count() != 1) ||
                    !TryString(root, "id", out var id) || !string.Equals(id, botId, StringComparison.Ordinal) ||
                    !TryString(root, "platform", out var platformValue) ||
                    !TryString(root, "user_id", out var userId) || !NyxChannelBotIdentity.IsValid(userId) ||
                    !TryString(root, "status", out var status) ||
                    !root.TryGetProperty("is_active", out var active) ||
                    active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return new(null, "invalid_channel_bot_detail");
                var platform = ChannelPlatformId.ParseExternal(platformValue);
                if (!string.IsNullOrEmpty(assertedPlatform) &&
                    !platform.Equals(ChannelPlatformId.FromCanonical(assertedPlatform)))
                    return new(null, "channel_bot_platform_mismatch");
                if (!string.Equals(userId, owner.KeyOwner.Id, StringComparison.Ordinal))
                    return new(null, "channel_bot_not_found_or_forbidden");
                if (!active.GetBoolean() || status is not ("active" or "pending_webhook"))
                    return new(null, "channel_bot_not_adoptable");
                TryString(root, "webhook_url", out var webhookUrl);
                return new(new VerifiedNyxChannelBotDetail(id, platform, webhookUrl, owner), string.Empty);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ArgumentException) { return new(null, "invalid_channel_bot_detail"); }
            catch (JsonException) { return new(null, "invalid_channel_bot_detail"); }
            catch (Exception) { return new(null, "channel_bot_not_found_or_forbidden"); }
        }

        private static bool TryString(JsonElement root, string name, out string value)
        {
            value = string.Empty;
            if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
                return false;
            value = property.GetString() ?? string.Empty;
            return true;
        }
    }
}

public sealed record NyxChannelBotAdoptionRequest(
    VerifiedNyxChannelBotDetail Bot,
    ChannelRelayRegistrationRequest Registration);

public sealed record NyxChannelBotAdoptionResult(
    bool Succeeded,
    string Status,
    string Platform,
    string? RegistrationId = null,
    string? NyxChannelBotId = null,
    string? NyxAgentApiKeyId = null,
    string? NyxConversationRouteId = null,
    bool WorkflowResultDeliveryEnabled = false,
    string? RelayCallbackUrl = null,
    string? WebhookUrl = null,
    string? Error = null,
    string? Note = null,
    string? ErrorDetail = null,
    NyxChannelBotDeprovisioningResult? Cleanup = null,
    NyxChannelBotDeprovisioningRequest? CleanupRequest = null,
    ChannelRegistrationCommandAcceptedReceipt? Receipt = null,
    string? NyxProviderSlug = null,
    string? ExistingRegistrationId = null);

public interface INyxChannelBotAdoptionService
{
    Task<NyxChannelBotAdoptionResult> AdoptAsync(NyxChannelBotAdoptionRequest request, CancellationToken ct);
}

/// <summary>Creates only a dedicated registration Key, Vault reference and Bot route.</summary>
public sealed class NyxChannelBotAdoptionService(
    NyxIdApiClient client,
    ChannelRegistrationCommandFacade commandFacade,
    ChannelAgentKeyProvisioningService keys,
    INyxChannelBotDeprovisioningService deprovisioning,
    ILogger<NyxChannelBotAdoptionService> logger,
    ChannelRegistrationExplicitAuthorizationPlanner? authorizationPlanner = null) : INyxChannelBotAdoptionService
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    public async Task<NyxChannelBotAdoptionResult> AdoptAsync(NyxChannelBotAdoptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Bot);
        var bot = request.Bot;
        var registration = request.Registration;
        var platform = bot.Platform.Value;
        if (!NyxChannelBotIdentity.IsValid(registration.NyxChannelBotId) ||
            !string.Equals(registration.NyxChannelBotId, bot.Id, StringComparison.Ordinal))
            return Failure("missing_nyx_channel_bot_id");
        if (!string.Equals(registration.Platform, platform, StringComparison.Ordinal) ||
            !string.Equals(registration.ScopeId, bot.Owner.KeyOwner.Id, StringComparison.Ordinal))
            return Failure("invalid_channel_bot_detail");
        var registrationId = registration.RequestedRegistrationId ?? Guid.NewGuid().ToString("N");
        var relayCallbackUrl = NyxRelayCallbackUrl.Build(registration.WebhookBaseUrl);
        string? routeId = null;
        NyxChannelRouteAcquisitionUncertainty? uncertainRouteAcquisition = null;
        ChannelAgentKeyCredential? key = null;
        try
        {
            // NyxID creates UUID routes and deactivates older defaults. There is no atomic
            // idempotency key. Never replace or adopt another registration's default route.
            var routes = await client.ListConversationRoutesAsync(registration.AccessToken, bot.Id,
                bot.Owner.TargetOrganizationId, ct);
            if (!HasNoActiveDefaultRoute(routes, bot.Id))
                return Failure("channel_route_not_accessible");
            VerifiedChannelRegistrationExplicitAuthorization? authorization = null;
            if (registration.ServiceSelection.AuthorizationMode == ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)
            {
                if (authorizationPlanner is null)
                    return Failure("nyxid_scope_plan_unavailable");
                var prepared = await authorizationPlanner.PlanAsync(registration, bot.Owner, ct);
                if (!prepared.Succeeded)
                    return Failure(prepared.ErrorCode);
                authorization = prepared.Authorization;
            }
            if (ChannelBotRuntimeConfigValidation.ValidateSelectorAuthorization(
                    registration.RuntimeConfig,
                    registration.ServiceSelection.AuthorizationMode,
                    authorization?.RuntimeSelectors.Select(static selector => selector.ServiceSlug).ToArray() ?? []).Count > 0)
                return Failure("invalid_runtime_config");
            key = authorization is null
                ? await keys.ProvisionAsync(platform, registration.AccessToken, relayCallbackUrl,
                    registration.ScopeId, registrationId, bot.Owner, ct)
                : await keys.ProvisionAsync(platform, registration.AccessToken, relayCallbackUrl,
                    registration.ScopeId, registrationId, authorization, ct);
            uncertainRouteAcquisition = new(bot.Id, bot.Owner.TargetOrganizationId);
            var response = await client.CreateConversationRouteAsync(registration.AccessToken,
                JsonSerializer.Serialize(new
                {
                    channel_bot_id = bot.Id,
                    agent_api_key_id = key.ApiKeyId,
                    default_agent = true,
                    target_org_id = bot.Owner.TargetOrganizationId,
                }), ct);
            if (IsConfirmedRouteRejection(response))
                uncertainRouteAcquisition = null;
            routeId = NyxApiResponseHelper.ExtractRequiredId(response, "channel_route_id");
            uncertainRouteAcquisition = null;
            var command = new ChannelBotRegisterCommand
            {
                RequestedId = registrationId,
                Platform = platform,
                ScopeId = registration.ScopeId,
                NyxProviderSlug = registration.NyxProviderSlug,
                WebhookUrl = bot.WebhookUrl,
                NyxAgentApiKeyId = key.ApiKeyId,
                NyxChannelBotId = bot.Id,
                NyxConversationRouteId = routeId,
                WorkflowResultDeliveryCredential = key.SecretReference.Clone(),
                ChannelAgentKey = key.Clone(),
                AuthorizationMode = registration.ServiceSelection.AuthorizationMode,
                DefaultSkillName = registration.DefaultSkillName,
                RuntimeConfig = ChannelRegistrationLocalMirrorRuntimeConfig.Build(
                    registration.RuntimeConfig, registration.DefaultSkillName, authorization),
            };
            if (authorization is not null)
            {
                command.RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist();
                command.RegistrationServiceAllowlist.ServiceIds.Add(authorization.Plan.RegistrationServiceIds);
            }
            if (!ChannelRegistrationAuthorizationContract.IsValidNewCommand(command))
                throw new InvalidOperationException("channel_authorization_contract_invalid");
            var receipt = await commandFacade.RegisterLocalMirrorAsync(command, ct);
            return new(true, "accepted", platform, registrationId, bot.Id, key.ApiKeyId, routeId,
                true, relayCallbackUrl, bot.WebhookUrl,
                Note: "Existing NyxID Bot adopted; the local registration command was accepted. Read model visibility is asynchronous.",
                Receipt: receipt,
                NyxProviderSlug: registration.NyxProviderSlug);
        }
        catch (Exception ex)
        {
            var acceptanceUnknown = ex is ChannelRegistrationCommandDispatchException { AcceptanceUnknown: true };
            var failure = acceptanceUnknown ? "local_mirror_acceptance_unknown_remote_cleanup_skipped" :
                NyxApiResponseHelper.SanitizeFailureReason(ex);
            logger.LogWarning("NyxID Bot adoption failed: registration={RegistrationId}, botId={BotId}, failureCode={FailureCode}, failureType={FailureType}",
                registrationId, bot.Id, failure, ex.GetType().Name);
            if (acceptanceUnknown)
                return new(false, "error", platform, registrationId, bot.Id, key?.ApiKeyId, routeId,
                    RelayCallbackUrl: relayCallbackUrl, WebhookUrl: bot.WebhookUrl, Error: failure,
                    Note: "Command acceptance is unknown. Owned resources were retained; observe the registration before retrying.");

            if (key is null && routeId is null)
                return Failure(failure);

            // Confirmed non-acceptance can use the existing owned-resource cleanup contract.
            // Retain its handles until cleanup is confirmed; no local registration was accepted.
            var cleanupRequest = new NyxChannelBotDeprovisioningRequest(
                registrationId, platform, routeId, key?.ApiKeyId, key?.SecretReference.Clone(),
                AgentKeyDeletionRequired: key is not null,
                UncertainRouteAcquisition: uncertainRouteAcquisition);
            NyxChannelBotDeprovisioningResult cleanupResult;
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            try
            {
                cleanupResult = await deprovisioning.DeprovisionAsync(
                    registration.AccessToken, cleanupRequest, cleanup.Token);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning("Owned adoption cleanup was interrupted: registration={RegistrationId}, failureType={FailureType}",
                    registrationId, cleanupException.GetType().Name);
                cleanupResult = new(false, false, ["owned_cleanup_interrupted"],
                    ConversationRouteRemoved: false, VaultSecretRevoked: false);
            }

            cleanupRequest = cleanupResult.RetryRequest ?? cleanupRequest;
            return new(false, "error", platform, registrationId, bot.Id, key?.ApiKeyId,
                routeId ?? cleanupRequest.ConversationRouteId,
                RelayCallbackUrl: relayCallbackUrl, WebhookUrl: bot.WebhookUrl, Error: failure,
                Note: cleanupResult.CleanupComplete
                    ? "Command was not accepted. Owned resource cleanup completed; the existing Bot can be adopted again."
                    : "Command was not accepted. Owned resource cleanup is incomplete; retry cleanup with the retained ownership handles before re-adopting the existing Bot.",
                Cleanup: cleanupResult,
                CleanupRequest: cleanupResult.CleanupComplete ? null : cleanupRequest);
        }

        NyxChannelBotAdoptionResult Failure(string error, string? detail = null) =>
            new(false, "error", platform, Error: error, ErrorDetail: detail);
    }

    private static bool IsConfirmedRouteRejection(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            // These NyxID validation/authorization/not-found responses precede insertion.
            // Transport failures and server errors can follow a committed route write.
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True &&
                   root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number &&
                   status.TryGetInt32(out var code) &&
                   code is 400 or 401 or 403 or 404;
        }
        catch (JsonException) { return false; }
    }

    private static bool HasNoActiveDefaultRoute(string response, string botId)
    {
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            return false;
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("conversations", out var routes) || routes.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var route in routes.EnumerateArray())
            {
                if (route.ValueKind != JsonValueKind.Object ||
                    !route.TryGetProperty("channel_bot_id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !string.Equals(id.GetString(), botId, StringComparison.Ordinal) ||
                    !route.TryGetProperty("default_agent", out var isDefault) ||
                    isDefault.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !route.TryGetProperty("is_active", out var active) ||
                    active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return false;
                if (isDefault.GetBoolean() && active.GetBoolean())
                    return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }
}
