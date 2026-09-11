using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationExplicitAuthorization;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record NyxTelegramProvisioningRequest(
    string AccessToken,
    string BotToken,
    string WebhookBaseUrl,
    string ScopeId,
    string Label,
    string NyxProviderSlug,
    string DefaultSkillName = "",
    ChannelBotRuntimeConfig? RuntimeConfig = null,
    ChannelRegistrationServiceSelection? RequestedServiceSelection = null);

public sealed record NyxTelegramProvisioningResult(
    bool Succeeded,
    string Status,
    string? RegistrationId = null,
    string? NyxChannelBotId = null,
    string? NyxAgentApiKeyId = null,
    string? NyxConversationRouteId = null,
    bool WorkflowResultDeliveryEnabled = false,
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
    // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
    //   Old pattern: Nyx relay registration endpoints + singleton provisioning services 在 Host 内做 platform selection / scope resolution / remote Nyx provisioning / actor creation / envelope construction / dispatch through raw runtime/dispatch helpers。
    //   New principle: Channel registration 暴露 typed application command facade(reuse existing CQRS command dispatch skeleton);Host 仅 adapt HTTP;provisioning adapters 只调 existing NyxID REST surfaces(**不修改 NyxID 仓库**);local mirror writes 进 standard command skeleton via narrow dispatch port。**不引入新 actor type / 新 envelope / 新 projection phase**(reflector force-pick minimal,排除 structural 的 ChannelRelayRegistrationRunGAgent)。
    private const string DefaultNyxProviderSlug = "api-telegram-bot";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    public const string PlatformId = "telegram";

    private readonly NyxIdApiClient _nyxClient;
    private readonly NyxIdToolOptions _nyxOptions;
    private readonly ChannelRegistrationCommandFacade _commandFacade;
    private readonly ChannelAgentKeyProvisioningService _channelAgentKeyProvisioning;
    private readonly IChannelRegistrationOwnerResolver _ownerResolver;
    private readonly ILogger<NyxTelegramProvisioningService> _logger;
    private readonly ChannelRegistrationExplicitAuthorizationPreparation? _authorizationPreparation;

    public NyxTelegramProvisioningService(
        NyxIdApiClient nyxClient,
        NyxIdToolOptions nyxOptions,
        ChannelRegistrationCommandFacade commandFacade,
        ChannelAgentKeyProvisioningService channelAgentKeyProvisioning,
        IChannelRegistrationOwnerResolver ownerResolver,
        ILogger<NyxTelegramProvisioningService> logger,
        ChannelRegistrationExplicitAuthorizationPreparation? authorizationPreparation = null)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _nyxOptions = nyxOptions ?? throw new ArgumentNullException(nameof(nyxOptions));
        _commandFacade = commandFacade ?? throw new ArgumentNullException(nameof(commandFacade));
        _channelAgentKeyProvisioning = channelAgentKeyProvisioning ??
            throw new ArgumentNullException(nameof(channelAgentKeyProvisioning));
        _ownerResolver = ownerResolver ?? throw new ArgumentNullException(nameof(ownerResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _authorizationPreparation = authorizationPreparation;
    }

    public string Platform => PlatformId;

    public async Task<NyxTelegramProvisioningResult> ProvisionAsync(NyxTelegramProvisioningRequest request, CancellationToken ct)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: Telegram provisioning service owned remote Nyx saga and raw local actor dispatch.
        //   New principle: provisioning only calls existing NyxID REST surfaces; local mirror command enters via facade.
        // Refactor (iter113/cluster-113-telegram-connector-inmemory-updates):
        //   Old pattern: Telegram connector keeps inbound updates as in-memory state (process-local queue/dictionary).
        //   New principle: Delete telegram_user /getUpdates in-memory queue and route inbound Telegram through existing NyxID relay/proxy; no new actor type; no in-memory state on connector side.
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return Failure("missing_access_token");
        if (string.IsNullOrWhiteSpace(request.BotToken))
            return Failure("missing_bot_token");
        if (string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
            return Failure("missing_webhook_base_url");
        if (!NyxRelayCallbackUrl.IsSecureBaseUrl(request.WebhookBaseUrl))
            return Failure("insecure_webhook_base_url");
        if (string.IsNullOrWhiteSpace(request.ScopeId))
            return Failure("missing_scope_id");
        if (string.IsNullOrWhiteSpace(_nyxOptions.EffectiveTransportBaseUrl))
            return Failure("nyx_base_url_not_configured");
        var publicApiBaseUrl = _nyxOptions.EffectiveApiBaseUrl;
        if (string.IsNullOrWhiteSpace(publicApiBaseUrl))
            return Failure("nyx_api_base_url_not_configured");

        var registrationId = Guid.NewGuid().ToString("N");
        var nyxBaseUrl = publicApiBaseUrl.TrimEnd('/');
        var relayCallbackUrl = NyxRelayCallbackUrl.Build(request.WebhookBaseUrl);
        var label = string.IsNullOrWhiteSpace(request.Label)
            ? $"Aevatar Telegram Bot {registrationId[..8]}"
            : request.Label.Trim();
        var nyxProviderSlug = string.IsNullOrWhiteSpace(request.NyxProviderSlug)
            ? DefaultNyxProviderSlug
            : request.NyxProviderSlug.Trim();

        string? apiKeyId = null;
        string? channelBotId = null;
        string? routeId = null;
        ChannelAgentKeyCredential? channelAgentKey = null;
        VerifiedChannelRegistrationExplicitAuthorization? explicitAuthorization = null;
        var localMirrorAccepted = false;

        try
        {
            var ownerResolution = await _ownerResolver.ResolveAsync(
                request.AccessToken,
                request.ScopeId,
                ct);
            if (!ownerResolution.Succeeded)
                return Failure(ownerResolution.ErrorCode);
            var owner = ownerResolution.Owner!;

            if (request.RequestedServiceSelection?.AuthorizationMode == ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)
            {
                if (_authorizationPreparation is null)
                    return Failure("nyxid_scope_plan_unavailable");
                var prepared = await _authorizationPreparation.PrepareAsync(new NyxChannelBotProvisioningRequest(
                    PlatformId, request.AccessToken, request.WebhookBaseUrl, request.ScopeId, label, nyxProviderSlug,
                    DefaultSkillName: request.DefaultSkillName,
                    RuntimeConfig: request.RuntimeConfig?.Clone(),
                    RequestedServiceSelection: request.RequestedServiceSelection), registrationId, owner, ct);
                if (!prepared.Succeeded)
                    return Failure(prepared.ErrorCode);
                explicitAuthorization = prepared.Preparation;
            }

            channelAgentKey = explicitAuthorization is not null
                ? await _channelAgentKeyProvisioning.ProvisionAsync(
                    PlatformId, request.AccessToken, relayCallbackUrl, request.ScopeId.Trim(),
                    registrationId, explicitAuthorization, ct)
                : await _channelAgentKeyProvisioning.ProvisionAsync(
                    PlatformId, request.AccessToken, relayCallbackUrl, request.ScopeId.Trim(),
                    registrationId, owner, ct);
            apiKeyId = channelAgentKey.ApiKeyId;

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
                channelAgentKey,
                channelBotId,
                routeId,
                request.DefaultSkillName,
                request.RuntimeConfig,
                explicitAuthorization,
                ct);
            localMirrorAccepted = true;

            return new NyxTelegramProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: registrationId,
                NyxChannelBotId: channelBotId,
                NyxAgentApiKeyId: apiKeyId,
                NyxConversationRouteId: routeId,
                WorkflowResultDeliveryEnabled: true,
                RelayCallbackUrl: relayCallbackUrl,
                WebhookUrl: webhookUrl,
                Note: "Provisioning completed in Nyx and the local mirror command was accepted. NyxID has already registered the Telegram webhook and secret_token with the Bot API; do not call setWebhook manually or you will overwrite NyxID's secret_token and break inbound verification. Local read model visibility is asynchronous.");
        }
        catch (Exception ex)
        {
            var acceptanceUnknown = ex is ChannelRegistrationCommandDispatchException
            {
                AcceptanceUnknown: true,
            };
            var failureReason = localMirrorAccepted
                ? "local_mirror_accepted_remote_cleanup_skipped"
                : acceptanceUnknown
                    ? "local_mirror_acceptance_unknown_remote_cleanup_skipped"
                    : NyxApiResponseHelper.SanitizeFailureReason(ex);
            _logger.LogWarning(
                "Nyx-backed Telegram provisioning failed: registration={RegistrationId}, botId={ChannelBotId}, apiKeyId={ApiKeyId}, routeId={RouteId}, failureCode={FailureCode}, failureType={FailureType}",
                registrationId,
                channelBotId,
                apiKeyId,
                routeId,
                failureReason,
                ex.GetType().Name);

            if (!localMirrorAccepted && !acceptanceUnknown)
            {
                using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
                if (routeId is not null)
                    await NyxApiResponseHelper.TryRollbackAsync(() => _nyxClient.DeleteConversationRouteAsync(request.AccessToken, routeId, cleanupCts.Token), "channel_route", routeId, _logger);
                if (channelBotId is not null)
                    await NyxApiResponseHelper.TryRollbackAsync(() => _nyxClient.DeleteChannelBotAsync(request.AccessToken, channelBotId, cleanupCts.Token), "channel_bot", channelBotId, _logger);
                if (channelAgentKey is not null)
                    await _channelAgentKeyProvisioning.CleanupAsync(request.AccessToken, channelAgentKey, registrationId, cleanupCts.Token);
                if (explicitAuthorization?.Connection is { } connection)
                    await _authorizationPreparation!.CleanupConnectionAsync(request.AccessToken, connection);
            }

            return Failure(failureReason);
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
                NyxProviderSlug: request.NyxProviderSlug,
                DefaultSkillName: request.DefaultSkillName,
                RuntimeConfig: request.RuntimeConfigCopy,
                RequestedServiceSelection: request.ServiceSelection),
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
        ChannelAgentKeyCredential channelAgentKey,
        string channelBotId,
        string routeId,
        string defaultSkillName,
        ChannelBotRuntimeConfig? runtimeConfig,
        VerifiedChannelRegistrationExplicitAuthorization? authorization,
        CancellationToken ct)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: provisioning service injected runtime/dispatch and hand-built local mirror dispatch.
        //   New principle: local mirror write enters the typed application command facade only.
        var cmd = new ChannelBotRegisterCommand
        {
            RequestedId = registrationId,
            Platform = PlatformId,
            NyxProviderSlug = nyxProviderSlug,
            ScopeId = scopeId,
            WebhookUrl = webhookUrl,
            NyxAgentApiKeyId = channelAgentKey.ApiKeyId,
            NyxChannelBotId = channelBotId,
            NyxConversationRouteId = routeId,
            WorkflowResultDeliveryCredential = channelAgentKey.SecretReference.Clone(),
            ChannelAgentKey = channelAgentKey.Clone(),
            AuthorizationMode = authorization is null
                ? ChannelRegistrationAuthorizationMode.NyxidDefault
                : ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist,
            DefaultSkillName = defaultSkillName ?? string.Empty,
            RuntimeConfig = runtimeConfig?.Clone(),
        };

        if (authorization is not null)
        {
            cmd.RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist();
            cmd.RegistrationServiceAllowlist.ServiceIds.Add(authorization.Plan.RegistrationServiceIds);
        }
        if (!ChannelRegistrationAuthorizationContract.IsValidNewCommand(cmd))
            throw new InvalidOperationException("channel_authorization_contract_invalid");
        await _commandFacade.RegisterLocalMirrorAsync(cmd, ct);
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
            WorkflowResultDeliveryEnabled: result.WorkflowResultDeliveryEnabled,
            RelayCallbackUrl: result.RelayCallbackUrl,
            WebhookUrl: result.WebhookUrl,
            Error: result.Error,
            Note: result.Note);
}
