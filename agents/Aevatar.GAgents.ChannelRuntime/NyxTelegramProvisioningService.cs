using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed record NyxTelegramProvisioningRequest(
    string AccessToken,
    string BotToken,
    string WebhookBaseUrl,
    string ScopeId,
    string Label,
    string NyxProviderSlug);

public sealed record NyxTelegramProvisioningResult(
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

public interface INyxTelegramProvisioningService
{
    string Platform { get; }

    Task<NyxTelegramProvisioningResult> ProvisionAsync(NyxTelegramProvisioningRequest request, CancellationToken ct);
}

public sealed class NyxTelegramProvisioningService : INyxTelegramProvisioningService, INyxChannelBotProvisioningService
{
    private const string DefaultNyxProviderSlug = "api-telegram-bot";
    private const string NyxRelayApiKeyPlatform = "generic";
    public const string PlatformId = "telegram";

    private readonly NyxIdApiClient _nyxClient;
    private readonly NyxIdToolOptions _nyxOptions;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ILogger<NyxTelegramProvisioningService> _logger;

    private sealed record RelayApiKeyCredentials(string Id);

    public NyxTelegramProvisioningService(
        NyxIdApiClient nyxClient,
        NyxIdToolOptions nyxOptions,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        ILogger<NyxTelegramProvisioningService> logger)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _nyxOptions = nyxOptions ?? throw new ArgumentNullException(nameof(nyxOptions));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Platform => PlatformId;

    public async Task<NyxTelegramProvisioningResult> ProvisionAsync(NyxTelegramProvisioningRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return Failure("missing_access_token");
        if (string.IsNullOrWhiteSpace(request.BotToken))
            return Failure("missing_bot_token");
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
            ? $"Aevatar Telegram Bot {registrationId[..8]}"
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
                request.BotToken,
                label,
                ct);
            routeId = await CreateDefaultRouteAsync(request.AccessToken, channelBotId, apiKeyId, ct);

            var webhookUrl = $"{nyxBaseUrl}/api/v1/webhooks/channel/telegram/{Uri.EscapeDataString(channelBotId)}";
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

            return new NyxTelegramProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: registrationId,
                NyxChannelBotId: channelBotId,
                NyxAgentApiKeyId: apiKeyId,
                NyxConversationRouteId: routeId,
                RelayCallbackUrl: relayCallbackUrl,
                WebhookUrl: webhookUrl,
                Note: "Provisioning completed in Nyx and the local mirror command was accepted. Configure the Telegram bot webhook URL via setWebhook to point at Nyx; local read model visibility is asynchronous.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Nyx-backed Telegram provisioning failed: registration={RegistrationId}, botId={ChannelBotId}, apiKeyId={ApiKeyId}, routeId={RouteId}",
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

    async Task<NyxChannelBotProvisioningResult> INyxChannelBotProvisioningService.ProvisionAsync(
        NyxChannelBotProvisioningRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Platform, PlatformId, StringComparison.OrdinalIgnoreCase))
            return ToGenericResult(Failure("unsupported_platform"));

        // The credentials map is the canonical platform-extensible carrier; absent map is a 400.
        var botToken = ResolveBotToken(request);
        if (string.IsNullOrWhiteSpace(botToken))
            return ToGenericResult(Failure("missing_bot_token"));

        var result = await ProvisionAsync(
            new NyxTelegramProvisioningRequest(
                AccessToken: request.AccessToken,
                BotToken: botToken,
                WebhookBaseUrl: request.WebhookBaseUrl,
                ScopeId: request.ScopeId,
                Label: request.Label,
                NyxProviderSlug: request.NyxProviderSlug),
            ct);

        return ToGenericResult(result);
    }

    private static string ResolveBotToken(NyxChannelBotProvisioningRequest request)
    {
        if (request.Credentials is { } credentials &&
            credentials.TryGetValue("bot_token", out var fromMap) &&
            !string.IsNullOrWhiteSpace(fromMap))
        {
            return fromMap.Trim();
        }

        return string.Empty;
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
                name = $"aevatar-telegram-relay-{registrationId[..12]}",
                scopes = "read write",
                platform = NyxRelayApiKeyPlatform,
                callback_url = relayCallbackUrl,
            }),
            ct);

        return new RelayApiKeyCredentials(NyxApiResponseHelper.ExtractRequiredApiKeyId(response));
    }

    private async Task<string> RegisterChannelBotAsync(
        string accessToken,
        string botToken,
        string label,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["platform"] = PlatformId,
            ["bot_token"] = botToken.Trim(),
            ["label"] = label,
        };

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
            Platform = PlatformId,
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

    private static NyxTelegramProvisioningResult Failure(string error) =>
        new(
            Succeeded: false,
            Status: "error",
            Error: string.IsNullOrWhiteSpace(error) ? "unknown_error" : error.Trim());

    private static NyxChannelBotProvisioningResult ToGenericResult(NyxTelegramProvisioningResult result) =>
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
