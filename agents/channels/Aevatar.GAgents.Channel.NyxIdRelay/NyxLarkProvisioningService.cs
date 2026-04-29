using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record NyxLarkProvisioningRequest(
    string AccessToken,
    string AppId,
    string AppSecret,
    string VerificationToken,
    string WebhookBaseUrl,
    string ScopeId,
    string Label,
    string NyxProviderSlug);

public sealed record NyxLarkMirrorRepairRequest(
    string AccessToken,
    string RequestedRegistrationId,
    string ScopeId,
    string NyxProviderSlug,
    string WebhookBaseUrl,
    string NyxChannelBotId,
    string NyxAgentApiKeyId,
    string NyxConversationRouteId);

public sealed record NyxLarkProvisioningResult(
    bool Succeeded,
    string Status,
    string? RegistrationId = null,
    string? NyxChannelBotId = null,
    string? NyxAgentApiKeyId = null,
    string? NyxConversationRouteId = null,
    string? RelayCallbackUrl = null,
    string? WebhookUrl = null,
    string? Error = null,
    string? Note = null);

public sealed record NyxLarkMirrorRepairResult(
    bool Succeeded,
    string Status,
    string? RegistrationId = null,
    string? NyxChannelBotId = null,
    string? NyxAgentApiKeyId = null,
    string? NyxConversationRouteId = null,
    string? WebhookUrl = null,
    string? Error = null,
    string? Note = null);

public sealed record NyxChannelLarkCredentials(
    string AppId,
    string AppSecret,
    string VerificationToken);

public sealed record NyxChannelBotProvisioningRequest(
    string Platform,
    string AccessToken,
    string WebhookBaseUrl,
    string ScopeId,
    string Label,
    string NyxProviderSlug,
    NyxChannelLarkCredentials? Lark = null,
    IReadOnlyDictionary<string, string>? Credentials = null);

public sealed record NyxChannelBotProvisioningResult(
    bool Succeeded,
    string Status,
    string Platform,
    string? RegistrationId = null,
    string? NyxChannelBotId = null,
    string? NyxAgentApiKeyId = null,
    string? NyxConversationRouteId = null,
    string? RelayCallbackUrl = null,
    string? WebhookUrl = null,
    string? Error = null,
    string? Note = null);

public interface INyxChannelBotProvisioningService
{
    string Platform { get; }

    Task<NyxChannelBotProvisioningResult> ProvisionAsync(NyxChannelBotProvisioningRequest request, CancellationToken ct);
}

public interface INyxLarkProvisioningService
{
    string Platform { get; }

    Task<NyxLarkProvisioningResult> ProvisionAsync(NyxLarkProvisioningRequest request, CancellationToken ct);
    Task<NyxLarkMirrorRepairResult> RepairLocalMirrorAsync(NyxLarkMirrorRepairRequest request, CancellationToken ct);
}

public sealed class NyxLarkProvisioningService : INyxLarkProvisioningService, INyxChannelBotProvisioningService
{
    private const string DefaultNyxProviderSlug = "api-lark-bot";
    private const string LarkBotTokenPlaceholder = "__unused_for_lark__";
    private const string NyxRelayApiKeyPlatform = "generic";
    public const string PlatformId = "lark";

    private readonly NyxIdApiClient _nyxClient;
    private readonly NyxIdToolOptions _nyxOptions;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ILogger<NyxLarkProvisioningService> _logger;

    private sealed record RelayApiKeyCredentials(string Id);
    private sealed record ConfirmedRelayApiKey(string Id, string CallbackUrl);
    private sealed record ConfirmedChannelBot(string Id, string Platform, string WebhookUrl);
    private sealed record ConfirmedConversationRoute(string Id, string ChannelBotId, string AgentApiKeyId, bool DefaultAgent);

    public NyxLarkProvisioningService(
        NyxIdApiClient nyxClient,
        NyxIdToolOptions nyxOptions,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        ILogger<NyxLarkProvisioningService> logger)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _nyxOptions = nyxOptions ?? throw new ArgumentNullException(nameof(nyxOptions));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Platform => PlatformId;

    public async Task<NyxLarkProvisioningResult> ProvisionAsync(NyxLarkProvisioningRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return Failure("missing_access_token");
        if (string.IsNullOrWhiteSpace(request.AppId))
            return Failure("missing_app_id");
        if (string.IsNullOrWhiteSpace(request.AppSecret))
            return Failure("missing_app_secret");
        if (string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
            return Failure("missing_webhook_base_url");
        if (string.IsNullOrWhiteSpace(request.ScopeId))
            return Failure("missing_scope_id");
        if (string.IsNullOrWhiteSpace(_nyxOptions.BaseUrl))
            return Failure("nyx_base_url_not_configured");

        var registrationId = Guid.NewGuid().ToString("N");
        var nyxBaseUrl = _nyxOptions.BaseUrl.TrimEnd('/');
        var relayCallbackUrl = $"{request.WebhookBaseUrl.Trim().TrimEnd('/')}/api/webhooks/nyxid-relay";
        var label = string.IsNullOrWhiteSpace(request.Label)
            ? $"Aevatar Lark Bot {registrationId[..8]}"
            : request.Label.Trim();
        var nyxProviderSlug = string.IsNullOrWhiteSpace(request.NyxProviderSlug)
            ? DefaultNyxProviderSlug
            : request.NyxProviderSlug.Trim();

        string? apiKeyId = null;
        string? channelBotId = null;
        string? routeId = null;
        var localMirrorAccepted = false;

        try
        {
            var relayApiKey = await CreateRelayApiKeyAsync(request.AccessToken, relayCallbackUrl, registrationId, ct);
            apiKeyId = relayApiKey.Id;

            channelBotId = await RegisterChannelBotAsync(
                request.AccessToken,
                request.AppId,
                request.AppSecret,
                request.VerificationToken,
                label,
                ct);
            routeId = await CreateDefaultRouteAsync(request.AccessToken, channelBotId, apiKeyId, ct);

            await TryConnectLarkBotProxyServiceAsync(
                request.AccessToken,
                request.AppId.Trim(),
                request.AppSecret.Trim(),
                ct);

            var webhookUrl = $"{nyxBaseUrl}/api/v1/webhooks/channel/lark/{Uri.EscapeDataString(channelBotId)}";
            await RegisterLocalMirrorAsync(
                registrationId,
                nyxProviderSlug,
                webhookUrl,
                request.ScopeId?.Trim() ?? string.Empty,
                apiKeyId,
                channelBotId,
                routeId,
                ct);
            localMirrorAccepted = true;

            return new NyxLarkProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: registrationId,
                NyxChannelBotId: channelBotId,
                NyxAgentApiKeyId: apiKeyId,
                NyxConversationRouteId: routeId,
                RelayCallbackUrl: relayCallbackUrl,
                WebhookUrl: webhookUrl,
                Note: "Provisioning completed in Nyx and the local mirror command was accepted. Configure the Lark developer console webhook URL to point at Nyx; local read model visibility is asynchronous.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Nyx-backed Lark provisioning failed: registration={RegistrationId}, botId={ChannelBotId}, apiKeyId={ApiKeyId}, routeId={RouteId}",
                registrationId,
                channelBotId,
                apiKeyId,
                routeId);

            if (!localMirrorAccepted && routeId is not null)
                await NyxApiResponseHelper.TryRollbackAsync(() => _nyxClient.DeleteConversationRouteAsync(request.AccessToken, routeId, ct), "channel_route", routeId, _logger);
            if (!localMirrorAccepted && channelBotId is not null)
                await NyxApiResponseHelper.TryRollbackAsync(() => _nyxClient.DeleteChannelBotAsync(request.AccessToken, channelBotId, ct), "channel_bot", channelBotId, _logger);
            if (!localMirrorAccepted && apiKeyId is not null)
                await NyxApiResponseHelper.TryRollbackAsync(() => _nyxClient.DeleteApiKeyAsync(request.AccessToken, apiKeyId, ct), "api_key", apiKeyId, _logger);

            return Failure(localMirrorAccepted
                ? "local_mirror_accepted_remote_cleanup_skipped"
                : NyxApiResponseHelper.SanitizeFailureReason(ex));
        }
    }

    public async Task<NyxLarkMirrorRepairResult> RepairLocalMirrorAsync(NyxLarkMirrorRepairRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return MirrorFailure("missing_access_token");
        if (string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
            return MirrorFailure("missing_webhook_base_url");
        if (string.IsNullOrWhiteSpace(request.ScopeId))
            return MirrorFailure("missing_scope_id");
        if (string.IsNullOrWhiteSpace(request.NyxChannelBotId))
            return MirrorFailure("missing_nyx_channel_bot_id");
        if (string.IsNullOrWhiteSpace(request.NyxAgentApiKeyId))
            return MirrorFailure("missing_nyx_agent_api_key_id");
        if (string.IsNullOrWhiteSpace(_nyxOptions.BaseUrl))
            return MirrorFailure("nyx_base_url_not_configured");

        var registrationId = string.IsNullOrWhiteSpace(request.RequestedRegistrationId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestedRegistrationId.Trim();
        var nyxProviderSlug = string.IsNullOrWhiteSpace(request.NyxProviderSlug)
            ? DefaultNyxProviderSlug
            : request.NyxProviderSlug.Trim();
        var relayCallbackUrl = $"{request.WebhookBaseUrl.Trim().TrimEnd('/')}/api/webhooks/nyxid-relay";

        try
        {
            var confirmedApiKey = await GetConfirmedRelayApiKeyAsync(
                request.AccessToken,
                request.NyxAgentApiKeyId.Trim(),
                relayCallbackUrl,
                ct);
            var confirmedBot = await GetConfirmedLarkChannelBotAsync(
                request.AccessToken,
                request.NyxChannelBotId.Trim(),
                ct);
            var confirmedRoute = await ResolveConfirmedConversationRouteAsync(
                request.AccessToken,
                request.NyxConversationRouteId?.Trim() ?? string.Empty,
                confirmedBot.Id,
                confirmedApiKey.Id,
                ct);

            // Note: proxy service connection (api-lark-bot) is skipped during repair because
            // the repair request does not carry Lark app credentials (AppId/AppSecret).
            // The service was connected during the original ProvisionAsync call and is
            // reusable across registrations.

            await RegisterLocalMirrorAsync(
                registrationId,
                nyxProviderSlug,
                confirmedBot.WebhookUrl,
                request.ScopeId?.Trim() ?? string.Empty,
                confirmedApiKey.Id,
                confirmedBot.Id,
                confirmedRoute.Id,
                ct);

            return new NyxLarkMirrorRepairResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: registrationId,
                NyxChannelBotId: confirmedBot.Id,
                NyxAgentApiKeyId: confirmedApiKey.Id,
                NyxConversationRouteId: confirmedRoute.Id,
                WebhookUrl: confirmedBot.WebhookUrl,
                Note: "Existing Nyx relay resources were verified and the local Aevatar mirror command was accepted. Callback authentication uses NyxID callback JWT; no local relay credential is preserved.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Nyx-backed Lark local mirror repair failed: registration={RegistrationId}, botId={ChannelBotId}, apiKeyId={ApiKeyId}, routeId={RouteId}",
                registrationId,
                request.NyxChannelBotId,
                request.NyxAgentApiKeyId,
                request.NyxConversationRouteId);

            return MirrorFailure(NyxApiResponseHelper.SanitizeFailureReason(ex));
        }
    }

    async Task<NyxChannelBotProvisioningResult> INyxChannelBotProvisioningService.ProvisionAsync(
        NyxChannelBotProvisioningRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Platform, PlatformId, StringComparison.OrdinalIgnoreCase))
            return ToGenericResult(Failure("unsupported_platform"));

        var result = await ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: request.AccessToken,
                AppId: request.Lark?.AppId ?? string.Empty,
                AppSecret: request.Lark?.AppSecret ?? string.Empty,
                VerificationToken: request.Lark?.VerificationToken ?? string.Empty,
                WebhookBaseUrl: request.WebhookBaseUrl,
                ScopeId: request.ScopeId,
                Label: request.Label,
                NyxProviderSlug: request.NyxProviderSlug),
            ct);

        return ToGenericResult(result);
    }

    private async Task<RelayApiKeyCredentials> CreateRelayApiKeyAsync(
        string accessToken,
        string relayCallbackUrl,
        string registrationId,
        CancellationToken ct)
    {
        var response = await _nyxClient.CreateApiKeyAsync(
            accessToken,
            JsonSerializer.Serialize(new
            {
                name = $"aevatar-lark-relay-{registrationId[..12]}",
                scopes = "read write",
                platform = NyxRelayApiKeyPlatform,
                callback_url = relayCallbackUrl,
            }),
            ct);

        return new RelayApiKeyCredentials(NyxApiResponseHelper.ExtractRequiredApiKeyId(response));
    }

    private async Task<string> RegisterChannelBotAsync(
        string accessToken,
        string appId,
        string appSecret,
        string verificationToken,
        string label,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["platform"] = "lark",
            ["bot_token"] = LarkBotTokenPlaceholder,
            ["label"] = label,
            ["app_id"] = appId.Trim(),
            ["app_secret"] = appSecret.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(verificationToken))
            payload["verification_token"] = verificationToken.Trim();

        var response = await _nyxClient.RegisterChannelBotAsync(
            accessToken,
            JsonSerializer.Serialize(payload),
            ct);

        return NyxApiResponseHelper.ExtractRequiredId(response, "channel_bot_id");
    }

    private async Task<string> CreateDefaultRouteAsync(
        string accessToken,
        string channelBotId,
        string apiKeyId,
        CancellationToken ct)
    {
        var response = await _nyxClient.CreateConversationRouteAsync(
            accessToken,
            JsonSerializer.Serialize(new
            {
                channel_bot_id = channelBotId,
                agent_api_key_id = apiKeyId,
                default_agent = true,
            }),
            ct);

        return NyxApiResponseHelper.ExtractRequiredId(response, "channel_route_id");
    }

    private async Task TryConnectLarkBotProxyServiceAsync(
        string accessToken,
        string appId,
        string appSecret,
        CancellationToken ct)
    {
        try
        {
            var credential = JsonSerializer.Serialize(new { app_id = appId, app_secret = appSecret });
            var body = JsonSerializer.Serialize(new { service_slug = DefaultNyxProviderSlug, credential });
            await _nyxClient.CreateServiceAsync(accessToken, body, ct);
        }
        catch (Exception ex)
        {
            // Best-effort: 409 conflict (service already exists) or any other error is
            // non-fatal. The core relay path works without this; only typing reactions
            // are degraded when the proxy service is not connected.
            _logger.LogWarning(
                ex,
                "Best-effort api-lark-bot proxy service connection failed (non-fatal). appId={AppId}",
                appId);
        }
    }

    private async Task RegisterLocalMirrorAsync(
        string registrationId,
        string nyxProviderSlug,
        string webhookUrl,
        string scopeId,
        string apiKeyId,
        string channelBotId,
        string routeId,
        CancellationToken ct)
    {
        var cmd = new ChannelBotRegisterCommand
        {
            RequestedId = registrationId,
            Platform = "lark",
            NyxProviderSlug = nyxProviderSlug,
            ScopeId = scopeId,
            WebhookUrl = webhookUrl,
            NyxAgentApiKeyId = apiKeyId,
            NyxChannelBotId = channelBotId,
            NyxConversationRouteId = routeId,
        };

        await ChannelBotRegistrationStoreCommands.DispatchRegisterAsync(
            _actorRuntime,
            _dispatchPort,
            cmd,
            ct);
    }

    private async Task<ConfirmedRelayApiKey> GetConfirmedRelayApiKeyAsync(
        string accessToken,
        string apiKeyId,
        string expectedCallbackUrl,
        CancellationToken ct)
    {
        var response = await _nyxClient.GetApiKeyAsync(accessToken, apiKeyId, ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw new InvalidOperationException($"api_key_lookup_failed {NyxApiResponseHelper.ExtractErrorDetail(response)}");

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var confirmedId = ExtractRequiredString(root, "id", "api_key");
            var callbackUrl = ExtractRequiredString(root, "callback_url", "api_key");
            if (!string.Equals(NormalizeUrl(callbackUrl), NormalizeUrl(expectedCallbackUrl), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"api_key_callback_url_mismatch expected={NormalizeUrl(expectedCallbackUrl)} actual={NormalizeUrl(callbackUrl)}");
            }

            return new ConfirmedRelayApiKey(confirmedId, callbackUrl.Trim());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("invalid_json_in_api_key_lookup_response", ex);
        }
    }

    private async Task<ConfirmedChannelBot> GetConfirmedLarkChannelBotAsync(
        string accessToken,
        string channelBotId,
        CancellationToken ct)
    {
        var response = await _nyxClient.GetChannelBotAsync(accessToken, channelBotId, ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw new InvalidOperationException($"channel_bot_lookup_failed {NyxApiResponseHelper.ExtractErrorDetail(response)}");

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var confirmedId = ExtractRequiredString(root, "id", "channel_bot");
            var platform = ExtractOptionalString(root, "platform") ?? PlatformId;
            if (!string.Equals(platform, PlatformId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"unsupported_channel_bot_platform {platform}");

            var webhookUrl = ExtractOptionalString(root, "webhook_url");
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                webhookUrl = $"{_nyxOptions.BaseUrl!.Trim().TrimEnd('/')}/api/v1/webhooks/channel/lark/{Uri.EscapeDataString(confirmedId)}";
            }

            return new ConfirmedChannelBot(confirmedId, platform, webhookUrl.Trim());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("invalid_json_in_channel_bot_lookup_response", ex);
        }
    }

    private async Task<ConfirmedConversationRoute> ResolveConfirmedConversationRouteAsync(
        string accessToken,
        string requestedRouteId,
        string expectedChannelBotId,
        string expectedApiKeyId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedRouteId))
        {
            var response = await _nyxClient.GetConversationRouteAsync(accessToken, requestedRouteId, ct);
            if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
                throw new InvalidOperationException($"channel_route_lookup_failed {NyxApiResponseHelper.ExtractErrorDetail(response)}");

            return ParseConfirmedConversationRoute(response, expectedChannelBotId, expectedApiKeyId, "channel_route_lookup");
        }

        var listResponse = await _nyxClient.ListConversationRoutesAsync(accessToken, expectedChannelBotId, ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(listResponse))
            throw new InvalidOperationException($"channel_route_list_failed {NyxApiResponseHelper.ExtractErrorDetail(listResponse)}");

        var matches = ParseConversationRoutes(listResponse)
            .Where(route =>
                string.Equals(route.ChannelBotId, expectedChannelBotId, StringComparison.Ordinal) &&
                string.Equals(route.AgentApiKeyId, expectedApiKeyId, StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException("missing_matching_nyx_conversation_route");
        if (matches.Count == 1)
            return matches[0];

        var defaultMatches = matches.Where(static route => route.DefaultAgent).ToList();
        if (defaultMatches.Count == 1)
            return defaultMatches[0];

        throw new InvalidOperationException("ambiguous_matching_nyx_conversation_route");
    }

    private static ConfirmedConversationRoute ParseConfirmedConversationRoute(
        string response,
        string expectedChannelBotId,
        string expectedApiKeyId,
        string responseName)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            return ParseConversationRoute(document.RootElement, expectedChannelBotId, expectedApiKeyId, responseName);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"invalid_json_in_{responseName}_response", ex);
        }
    }

    private static IReadOnlyList<ConfirmedConversationRoute> ParseConversationRoutes(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var routes = new List<ConfirmedConversationRoute>();
            foreach (var item in EnumerateObjects(document.RootElement, "conversations", "routes", "channel_conversations", "items", "data"))
            {
                routes.Add(ParseConversationRoute(item, null, null, "channel_route_list"));
            }

            return routes;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("invalid_json_in_channel_route_list_response", ex);
        }
    }

    private static ConfirmedConversationRoute ParseConversationRoute(
        JsonElement element,
        string? expectedChannelBotId,
        string? expectedApiKeyId,
        string responseName)
    {
        var routeId = ExtractRequiredString(element, "id", responseName);
        var channelBotId = ExtractRequiredString(element, "channel_bot_id", responseName);
        var apiKeyId = ExtractRequiredString(element, "agent_api_key_id", responseName);
        var defaultAgent = ExtractOptionalBoolean(element, "default_agent") ?? false;

        if (expectedChannelBotId is not null &&
            !string.Equals(channelBotId, expectedChannelBotId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"channel_route_bot_mismatch expected={expectedChannelBotId} actual={channelBotId}");
        }

        if (expectedApiKeyId is not null &&
            !string.Equals(apiKeyId, expectedApiKeyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"channel_route_api_key_mismatch expected={expectedApiKeyId} actual={apiKeyId}");
        }

        return new ConfirmedConversationRoute(routeId, channelBotId, apiKeyId, defaultAgent);
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root, params string[] propertyNames)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in array.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    yield return item;
            yield break;
        }
    }

    private static string ExtractRequiredString(JsonElement element, string propertyName, string responseName)
    {
        var value = ExtractOptionalString(element, propertyName);
        if (value is null)
            throw new InvalidOperationException($"missing_{propertyName}_in_{responseName}_response");
        return value;
    }

    private static string? ExtractOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return NormalizeOptional(property.GetString());
    }

    private static bool? ExtractOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeUrl(string value) => value.Trim().TrimEnd('/');

    private static NyxLarkProvisioningResult Failure(string error) =>
        new(
            Succeeded: false,
            Status: "error",
            Error: string.IsNullOrWhiteSpace(error) ? "unknown_error" : error.Trim());

    private static NyxLarkMirrorRepairResult MirrorFailure(string error) =>
        new(
            Succeeded: false,
            Status: "error",
            Error: string.IsNullOrWhiteSpace(error) ? "unknown_error" : error.Trim());

    private static NyxChannelBotProvisioningResult ToGenericResult(NyxLarkProvisioningResult result) =>
        new(
            Succeeded: result.Succeeded,
            Status: result.Status,
            Platform: PlatformId,
            RegistrationId: result.RegistrationId,
            NyxChannelBotId: result.NyxChannelBotId,
            NyxAgentApiKeyId: result.NyxAgentApiKeyId,
            NyxConversationRouteId: result.NyxConversationRouteId,
            RelayCallbackUrl: result.RelayCallbackUrl,
            WebhookUrl: result.WebhookUrl,
            Error: result.Error,
            Note: result.Note);
}
