using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Configuration;
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

public sealed record NyxLarkProvisioningResult(
    bool Succeeded,
    string Status,
    string? RegistrationId = null,
    string? NyxChannelBotId = null,
    string? NyxAgentApiKeyId = null,
    string? NyxConversationRouteId = null,
    string? NyxReplyCredentialRef = null,
    string? RelayCallbackUrl = null,
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
    string? NyxReplyCredentialRef = null,
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
}

public sealed class NyxLarkProvisioningService : INyxLarkProvisioningService, INyxChannelBotProvisioningService
{
    // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
    //   Old pattern: Nyx relay registration endpoints + singleton provisioning services 在 Host 内做 platform selection / scope resolution / remote Nyx provisioning / actor creation / envelope construction / dispatch through raw runtime/dispatch helpers。
    //   New principle: Channel registration 暴露 typed application command facade(reuse existing CQRS command dispatch skeleton);Host 仅 adapt HTTP;provisioning adapters 只调 existing NyxID REST surfaces(**不修改 NyxID 仓库**);local mirror writes 进 standard command skeleton via narrow dispatch port。**不引入新 actor type / 新 envelope / 新 projection phase**(reflector force-pick minimal,排除 structural 的 ChannelRelayRegistrationRunGAgent)。
    private const string DefaultNyxProviderSlug = "api-lark-bot";
    private const string LarkBotTokenPlaceholder = "__unused_for_lark__";
    private const string NyxRelayApiKeyPlatform = "generic";
    public const string PlatformId = "lark";

    private readonly NyxIdApiClient _nyxClient;
    private readonly NyxIdToolOptions _nyxOptions;
    private readonly ChannelRegistrationCommandFacade _commandFacade;
    private readonly IAevatarSecretsStore _secretsStore;
    private readonly ILogger<NyxLarkProvisioningService> _logger;

    public NyxLarkProvisioningService(
        NyxIdApiClient nyxClient,
        NyxIdToolOptions nyxOptions,
        ChannelRegistrationCommandFacade commandFacade,
        IAevatarSecretsStore secretsStore,
        ILogger<NyxLarkProvisioningService> logger)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _nyxOptions = nyxOptions ?? throw new ArgumentNullException(nameof(nyxOptions));
        _commandFacade = commandFacade ?? throw new ArgumentNullException(nameof(commandFacade));
        _secretsStore = secretsStore ?? throw new ArgumentNullException(nameof(secretsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Platform => PlatformId;

    public async Task<NyxLarkProvisioningResult> ProvisionAsync(NyxLarkProvisioningRequest request, CancellationToken ct)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: Lark provisioning service owned remote Nyx saga and raw local actor dispatch.
        //   New principle: provisioning only calls existing NyxID REST surfaces; local mirror command enters via facade.
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
        string? replyCredentialRef = null;
        var localMirrorAccepted = false;

        try
        {
            var relayApiKeyResponse = await CreateRelayApiKeyAsync(request.AccessToken, relayCallbackUrl, registrationId, ct);
            apiKeyId = NyxApiResponseHelper.ExtractRequiredApiKeyId(relayApiKeyResponse);
            var relayApiKey = NyxApiResponseHelper.ExtractRequiredApiKeyCredentials(relayApiKeyResponse);
            replyCredentialRef = PersistRelayApiKeySecret(registrationId, relayApiKey);

            channelBotId = await RegisterChannelBotAsync(
                request.AccessToken,
                request.AppId,
                request.AppSecret,
                request.VerificationToken,
                label,
                ct);
            routeId = await CreateDefaultRouteAsync(request.AccessToken, channelBotId, apiKeyId, ct);

            // Best-effort: connect the api-lark-bot NyxID proxy service so typing
            // reactions can call the Lark API. Intentionally NOT in the rollback chain
            // because the service is reusable across registrations; a 409 on re-provision
            // is the expected idempotent case, not an orphan to clean up.
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
                replyCredentialRef,
                ct);
            localMirrorAccepted = true;

            return new NyxLarkProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: registrationId,
                NyxChannelBotId: channelBotId,
                NyxAgentApiKeyId: apiKeyId,
                NyxConversationRouteId: routeId,
                NyxReplyCredentialRef: replyCredentialRef,
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

    private async Task<string> CreateRelayApiKeyAsync(
        string accessToken,
        string relayCallbackUrl,
        string registrationId,
        CancellationToken ct)
    {
        return await _nyxClient.CreateApiKeyAsync(
            accessToken,
            JsonSerializer.Serialize(new
            {
                name = $"aevatar-lark-relay-{registrationId[..12]}",
                scopes = "read write",
                platform = NyxRelayApiKeyPlatform,
                callback_url = relayCallbackUrl,
            }),
            ct);
    }

    private string PersistRelayApiKeySecret(string registrationId, NyxRelayApiKeyCredentials credentials)
    {
        var secretRef = $"secrets://channel/nyxid/lark/{registrationId}/reply-api-key";
        _secretsStore.Set(secretRef, credentials.FullKey);
        return secretRef;
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
            var body = JsonSerializer.Serialize(new { service_slug = DefaultNyxProviderSlug, credential, label = $"Lark App {appId}" });
            await _nyxClient.CreateServiceAsync(accessToken, body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        string replyCredentialRef,
        CancellationToken ct)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: provisioning service injected runtime/dispatch and hand-built local mirror dispatch.
        //   New principle: local mirror write enters the typed application command facade only.
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
            NyxReplyCredentialRef = replyCredentialRef,
        };

        await _commandFacade.RegisterLocalMirrorAsync(cmd, ct);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static NyxLarkProvisioningResult Failure(string error) =>
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
            NyxReplyCredentialRef: result.NyxReplyCredentialRef,
            RelayCallbackUrl: result.RelayCallbackUrl,
            WebhookUrl: result.WebhookUrl,
            Error: result.Error,
            Note: result.Note);
}
