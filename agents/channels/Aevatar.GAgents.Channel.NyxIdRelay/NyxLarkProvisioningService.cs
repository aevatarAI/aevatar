using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
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
    string NyxProviderSlug,
    string DefaultSkillName = "",
    string EncryptKey = "");

public sealed record NyxLarkProvisioningResult(
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

public sealed record NyxChannelLarkCredentials(
    string AppId,
    string AppSecret,
    string VerificationToken,
    string EncryptKey = "");

public sealed record NyxChannelBotProvisioningRequest(
    string Platform,
    string AccessToken,
    string WebhookBaseUrl,
    string ScopeId,
    string Label,
    string NyxProviderSlug,
    NyxChannelLarkCredentials? Lark = null,
    IReadOnlyDictionary<string, string>? Credentials = null,
    string DefaultSkillName = "");

public sealed record NyxChannelBotProvisioningResult(
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
    private readonly ISecretVault _secretVault;
    private readonly ILogger<NyxLarkProvisioningService> _logger;

    public NyxLarkProvisioningService(
        NyxIdApiClient nyxClient,
        NyxIdToolOptions nyxOptions,
        ChannelRegistrationCommandFacade commandFacade,
        ISecretVault secretVault,
        ILogger<NyxLarkProvisioningService> logger)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _nyxOptions = nyxOptions ?? throw new ArgumentNullException(nameof(nyxOptions));
        _commandFacade = commandFacade ?? throw new ArgumentNullException(nameof(commandFacade));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
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
        if (string.IsNullOrWhiteSpace(request.VerificationToken))
            return Failure("missing_verification_token");
        if (string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
            return Failure("missing_webhook_base_url");
        if (!NyxRelayCallbackUrl.IsSecureBaseUrl(request.WebhookBaseUrl))
            return Failure("insecure_webhook_base_url");
        if (string.IsNullOrWhiteSpace(request.ScopeId))
            return Failure("missing_scope_id");
        if (string.IsNullOrWhiteSpace(_nyxOptions.BaseUrl))
            return Failure("nyx_base_url_not_configured");

        var registrationId = Guid.NewGuid().ToString("N");
        var nyxBaseUrl = _nyxOptions.BaseUrl.TrimEnd('/');
        var relayCallbackUrl = NyxRelayCallbackUrl.Build(request.WebhookBaseUrl);
        var label = string.IsNullOrWhiteSpace(request.Label)
            ? $"Aevatar Lark Bot {registrationId[..8]}"
            : request.Label.Trim();
        var requestedProviderSlug = string.IsNullOrWhiteSpace(request.NyxProviderSlug)
            ? DefaultNyxProviderSlug
            : request.NyxProviderSlug.Trim();

        string? apiKeyId = null;
        string? channelBotId = null;
        string? routeId = null;
        SecretReference? workflowResultDeliveryCredential = null;
        var localMirrorAccepted = false;

        try
        {
            var relayApiKeyResponse = await CreateRelayApiKeyAsync(request.AccessToken, relayCallbackUrl, registrationId, ct);
            apiKeyId = NyxApiResponseHelper.ExtractRequiredApiKeyId(relayApiKeyResponse);
            // The one-time NyxID full_key goes ONLY into the distributed secret vault; the local
            // mirror persists just the typed SecretReference handle. The interactive relay reply
            // path keeps authenticating via NyxID-issued reply tokens; the vault handle exists so
            // WorkflowRunDeliveryGAgent can deliver workflow terminal results in the background.
            // The mainnet host's IAevatarSecretsStore stays read-only and untouched (its write was
            // the original registration 502).
            workflowResultDeliveryCredential = await StoreWorkflowResultDeliveryCredentialAsync(
                relayApiKeyResponse,
                request.ScopeId.Trim(),
                apiKeyId,
                registrationId,
                ct);

            // Re-bind support: a fresh bot creates cleanly on the first try. But re-registering the
            // SAME Lark app hits NyxID's 409 already-exists (a stale channel-bot from the prior
            // registration), which previously aborted the whole flow as a 502 in the /channels
            // wizard. On that conflict, delete the stale channel-bot(s) for this app on the user's
            // behalf and retry once — so a re-bind completes without manual NyxID cleanup.
            try
            {
                channelBotId = await RegisterChannelBotAsync(
                    request.AccessToken,
                    request.AppId,
                    request.AppSecret,
                    request.VerificationToken,
                    request.EncryptKey,
                    label,
                    ct);
            }
            catch (InvalidOperationException ex) when (IndicatesChannelBotAlreadyExists(ex))
            {
                _logger.LogInformation(
                    "Nyx channel-bot already exists for Lark app; replacing it and retrying registration: appId={AppId}",
                    request.AppId);
                await RemoveExistingLarkChannelBotsForAppAsync(request.AccessToken, request.AppId, ct);
                channelBotId = await RegisterChannelBotAsync(
                    request.AccessToken,
                    request.AppId,
                    request.AppSecret,
                    request.VerificationToken,
                    request.EncryptKey,
                    label,
                    ct);
            }
            routeId = await CreateDefaultRouteAsync(request.AccessToken, channelBotId, apiKeyId, ct);

            // Connect the api-lark-bot NyxID proxy service (so card/typing calls can reach the Lark
            // API) and capture the per-connection slug NyxID assigned. When the user already has an
            // `api-lark-bot` connection, NyxID auto-numbers this one (api-lark-bot-2/-3...); storing
            // that returned slug — not the generic default — is what makes a later reply proxy
            // through THIS bot's own Lark app instead of always the first one (multi-bot cross-talk).
            // Intentionally NOT in the rollback chain: the connection is reusable across
            // registrations and a failure (incl. 409) is non-fatal — it just falls back to the
            // requested slug, degrading only this bot's outbound app binding, not the relay path.
            var connectedProviderSlug = await ConnectLarkBotProxyServiceAsync(
                request.AccessToken,
                requestedProviderSlug,
                request.AppId.Trim(),
                request.AppSecret.Trim(),
                ct);
            var nyxProviderSlug = connectedProviderSlug ?? requestedProviderSlug;

            var webhookUrl = $"{nyxBaseUrl}/api/v1/webhooks/channel/lark/{Uri.EscapeDataString(channelBotId)}";
            await RegisterLocalMirrorAsync(
                registrationId,
                nyxProviderSlug,
                webhookUrl,
                request.ScopeId?.Trim() ?? string.Empty,
                apiKeyId,
                channelBotId,
                routeId,
                workflowResultDeliveryCredential,
                request.DefaultSkillName,
                ct);
            localMirrorAccepted = true;

            return new NyxLarkProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: registrationId,
                NyxChannelBotId: channelBotId,
                NyxAgentApiKeyId: apiKeyId,
                NyxConversationRouteId: routeId,
                WorkflowResultDeliveryEnabled: workflowResultDeliveryCredential is not null,
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

            // Compensation runs detached from the caller's token: when the triggering failure IS
            // the caller's cancellation, the same token would abort every rollback delete and leave
            // a live NyxID api key whose full_key stays resolvable in the vault with no
            // registration referencing it.
            if (!localMirrorAccepted && routeId is not null)
                await NyxApiResponseHelper.TryRollbackAsync(() => _nyxClient.DeleteConversationRouteAsync(request.AccessToken, routeId, CancellationToken.None), "channel_route", routeId, _logger);
            if (!localMirrorAccepted && channelBotId is not null)
                await NyxApiResponseHelper.TryRollbackAsync(() => _nyxClient.DeleteChannelBotAsync(request.AccessToken, channelBotId, CancellationToken.None), "channel_bot", channelBotId, _logger);
            if (!localMirrorAccepted && workflowResultDeliveryCredential is not null && apiKeyId is not null)
                await TryRevokeWorkflowResultDeliveryCredentialAsync(workflowResultDeliveryCredential, apiKeyId, registrationId);
            if (!localMirrorAccepted && apiKeyId is not null)
                await NyxApiResponseHelper.TryRollbackAsync(() => _nyxClient.DeleteApiKeyAsync(request.AccessToken, apiKeyId, CancellationToken.None), "api_key", apiKeyId, _logger);

            return Failure(localMirrorAccepted
                ? "local_mirror_accepted_remote_cleanup_skipped"
                : NyxApiResponseHelper.SanitizeFailureReason(ex));
        }
    }

    /// <summary>
    /// Captures the one-time NyxID <c>full_key</c> from the create-api-key response into the
    /// distributed secret vault and returns the typed handle the registration persists. The raw
    /// key never enters Protobuf state, events, read models, results, or logs. Degrades to
    /// <c>null</c> (bot provisioned without workflow result delivery; workflow starts fail
    /// closed) when the response carries no full_key or the vault write fails — chat relay
    /// replies do not depend on this credential.
    /// </summary>
    private async Task<SecretReference?> StoreWorkflowResultDeliveryCredentialAsync(
        string relayApiKeyResponse,
        string scopeId,
        string apiKeyId,
        string registrationId,
        CancellationToken ct)
    {
        var fullKey = NyxApiResponseHelper.ExtractOptionalApiKeyFullKey(relayApiKeyResponse);
        if (fullKey is null)
        {
            _logger.LogWarning(
                "NyxID create-api-key response carried no full_key; Lark bot is provisioned without workflow result delivery: reason=credential_material_unavailable registration={RegistrationId}",
                registrationId);
            return null;
        }

        try
        {
            var stored = await _secretVault.PutAsync(
                new StoreSecretRequest(
                    CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
                    scopeId,
                    apiKeyId,
                    fullKey,
                    $"lark-channel-bot-provisioning:{registrationId}"),
                ct);
            return stored.Reference;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Secret vault write for the workflow result delivery agent key failed; Lark bot is provisioned without workflow result delivery: reason=credential_vault_put_failed registration={RegistrationId}",
                registrationId);
            return null;
        }
    }

    /// <summary>
    /// Best-effort vault compensation when provisioning fails after the vault write. A failed
    /// revoke is logged and must never shadow the NyxID api-key delete that follows it in the
    /// rollback chain — deleting the api key is what makes an orphaned vault record inert.
    /// </summary>
    private async Task TryRevokeWorkflowResultDeliveryCredentialAsync(
        SecretReference credential,
        string apiKeyId,
        string registrationId)
    {
        try
        {
            await _secretVault.RevokeAsync(
                new RevokeSecretRequest(
                    credential.Ref,
                    credential.Purpose,
                    credential.OwnerScopeKey,
                    apiKeyId,
                    $"lark-channel-bot-provisioning-rollback:{registrationId}"),
                CancellationToken.None);
        }
        // No OperationCanceledException carve-out: the revoke runs detached from the caller's
        // token, so any exception here (including a vault-internal timeout) must not shadow the
        // api-key delete that follows in the rollback chain.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Secret vault revoke for the workflow result delivery agent key failed during provisioning rollback: reason=credential_vault_revoke_failed registration={RegistrationId}",
                registrationId);
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
                NyxProviderSlug: request.NyxProviderSlug,
                DefaultSkillName: request.DefaultSkillName,
                EncryptKey: request.Lark?.EncryptKey ?? string.Empty),
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

    /// <summary>
    /// True when a channel-bot creation failure is NyxID's "already exists" conflict for this Lark
    /// app (HTTP 409), i.e. the re-bind case that should delete the stale bot and retry — not a
    /// credential/transport error, which must surface as-is. The structured failure string carries
    /// <c>nyx_status=409</c> (see <see cref="NyxApiResponseHelper.ExtractErrorDetail"/>).
    /// </summary>
    private static bool IndicatesChannelBotAlreadyExists(InvalidOperationException ex) =>
        ex.Message.Contains("nyx_status=409", StringComparison.Ordinal) ||
        ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Best-effort removal of any existing Nyx channel-bot bound to the SAME Lark app before a
    /// (re-)registration creates a fresh one. NyxID rejects a second channel-bot for an app that
    /// already has one with 409 already-exists; deleting the stale bot first is what lets a user
    /// re-bind the same bot from /channels without manual NyxID cleanup. The list response carries
    /// only <c>id</c> + <c>platform</c>, not the per-app <c>platform_bot_id</c>, so each Lark bot's
    /// detail is fetched to confirm it belongs to THIS app before deleting — a bot for a different
    /// Lark app is never touched. Failures are logged and swallowed: a bot that could not be removed
    /// simply resurfaces as the create 409 it would have produced anyway, so this never makes a fresh
    /// registration worse.
    /// </summary>
    private async Task RemoveExistingLarkChannelBotsForAppAsync(string accessToken, string appId, CancellationToken ct)
    {
        var normalizedAppId = appId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedAppId))
            return;

        string listResponse;
        try
        {
            listResponse = await _nyxClient.ListChannelBotsAsync(accessToken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not list Nyx channel-bots before re-registration: appId={AppId}", normalizedAppId);
            return;
        }

        foreach (var candidateBotId in NyxApiResponseHelper.ExtractLarkChannelBotIds(listResponse))
        {
            string detailResponse;
            try
            {
                detailResponse = await _nyxClient.GetChannelBotAsync(accessToken, candidateBotId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not read Nyx channel-bot detail while resolving the conflicting Lark app bot: botId={BotId}, appId={AppId}",
                    candidateBotId,
                    normalizedAppId);
                continue;
            }

            if (!NyxApiResponseHelper.ChannelBotDetailMatchesApp(detailResponse, normalizedAppId))
                continue;

            _logger.LogInformation(
                "Replacing existing Nyx channel-bot for Lark app before re-registration: botId={BotId}, appId={AppId}",
                candidateBotId,
                normalizedAppId);
            await NyxApiResponseHelper.TryRollbackAsync(
                () => _nyxClient.DeleteChannelBotAsync(accessToken, candidateBotId, ct),
                "channel_bot_replace",
                candidateBotId,
                _logger);
        }
    }

    private async Task<string> RegisterChannelBotAsync(
        string accessToken,
        string appId,
        string appSecret,
        string verificationToken,
        string encryptKey,
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
        if (!string.IsNullOrWhiteSpace(encryptKey))
            payload["encrypt_key"] = encryptKey.Trim();

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

    /// <summary>
    /// Connects the requested per-app NyxID proxy service and returns the slug NyxID assigned to
    /// THIS connection (e.g. <c>api-lark-bot-3</c> when the base slug is already taken).
    /// That slug is stored as the registration's provider slug so every reply for this bot proxies
    /// through its own Lark app. Best-effort: any failure (incl. a 409 already-exists) returns
    /// <c>null</c> so the caller falls back to the requested slug - the relay path still works,
    /// only this bot's outbound app binding degrades.
    /// </summary>
    private async Task<string?> ConnectLarkBotProxyServiceAsync(
        string accessToken,
        string providerSlug,
        string appId,
        string appSecret,
        CancellationToken ct)
    {
        try
        {
            var credential = JsonSerializer.Serialize(new { app_id = appId, app_secret = appSecret });
            var body = JsonSerializer.Serialize(new { service_slug = providerSlug, credential, label = $"Lark App {appId}" });
            var response = await _nyxClient.CreateServiceAsync(accessToken, body, ct);
            return NyxApiResponseHelper.ExtractOptionalProxyUrlSlug(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Best-effort Lark bot proxy service connection failed (non-fatal). appId={AppId}, providerSlug={ProviderSlug}",
                appId,
                providerSlug);
            return null;
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
        SecretReference? workflowResultDeliveryCredential,
        string defaultSkillName,
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
            NyxAgentApiKeyId = apiKeyId,
            NyxChannelBotId = channelBotId,
            NyxConversationRouteId = routeId,
            WebhookUrl = webhookUrl,
            WorkflowResultDeliveryCredential = workflowResultDeliveryCredential?.Clone(),
            DefaultSkillName = defaultSkillName ?? string.Empty,
        };

        await _commandFacade.RegisterLocalMirrorAsync(cmd, ct);
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
            WorkflowResultDeliveryEnabled: result.WorkflowResultDeliveryEnabled,
            RelayCallbackUrl: result.RelayCallbackUrl,
            WebhookUrl: result.WebhookUrl,
            Error: result.Error,
            Note: result.Note);
}
