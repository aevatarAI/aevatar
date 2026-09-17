using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

internal sealed record ChannelRegistrationAdoptionRequest(
    string? RegistrationId,
    string NyxChannelBotId,
    string? NyxConversationRouteId,
    string? NyxProviderSlug,
    ChannelBotRuntimeConfig? RuntimeConfig);

internal sealed record ChannelRegistrationAdoptionResult(
    bool Succeeded,
    string ErrorCode,
    ChannelRegistrationCommandAcceptedReceipt? Receipt,
    string RegistrationId,
    string Platform,
    string NyxProviderSlug,
    string NyxChannelBotId,
    string NyxAgentApiKeyId,
    string NyxConversationRouteId,
    string RelayCallbackUrl,
    string WebhookUrl,
    string? ExistingRegistrationId = null)
{
    public static ChannelRegistrationAdoptionResult Failure(string errorCode, string? existingRegistrationId = null) =>
        new(false, errorCode, null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, existingRegistrationId);

    public static ChannelRegistrationAdoptionResult Accepted(
        ChannelRegistrationCommandAcceptedReceipt receipt,
        string registrationId,
        string platform,
        string nyxProviderSlug,
        string nyxChannelBotId,
        string nyxAgentApiKeyId,
        string nyxConversationRouteId,
        string relayCallbackUrl,
        string webhookUrl) =>
        new(true, string.Empty, receipt, registrationId, platform, nyxProviderSlug, nyxChannelBotId, nyxAgentApiKeyId, nyxConversationRouteId, relayCallbackUrl, webhookUrl);
}

internal sealed class ChannelRegistrationAdoptionFacade
{
    private readonly ChannelRegistrationCommandFacade _commandFacade;
    private readonly IChannelBotRegistrationQueryPort _queryPort;
    private readonly IChannelRegistrationOwnerResolver _ownerResolver;
    private readonly ChannelRegistrationAuthorizationPlanner _authorizationPlanner;
    private readonly ChannelAgentKeyProvisioningService _agentKeyProvisioning;
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<ChannelRegistrationAdoptionFacade> _logger;

    public ChannelRegistrationAdoptionFacade(
        ChannelRegistrationCommandFacade commandFacade,
        IChannelBotRegistrationQueryPort queryPort,
        IChannelRegistrationOwnerResolver ownerResolver,
        ChannelRegistrationAuthorizationPlanner authorizationPlanner,
        ChannelAgentKeyProvisioningService agentKeyProvisioning,
        NyxIdApiClient nyxClient,
        ILogger<ChannelRegistrationAdoptionFacade> logger)
    {
        _commandFacade = commandFacade ?? throw new ArgumentNullException(nameof(commandFacade));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _ownerResolver = ownerResolver ?? throw new ArgumentNullException(nameof(ownerResolver));
        _authorizationPlanner = authorizationPlanner ?? throw new ArgumentNullException(nameof(authorizationPlanner));
        _agentKeyProvisioning = agentKeyProvisioning ?? throw new ArgumentNullException(nameof(agentKeyProvisioning));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChannelRegistrationAdoptionResult> AdoptAsync(
        ChannelRegistrationAdoptionRequest request,
        ChannelRegistrationServiceSelection serviceSelection,
        string accessToken,
        string scopeId,
        string webhookBaseUrl,
        CancellationToken ct)
    {
        var nyxChannelBotId = request.NyxChannelBotId.Trim();
        var bot = await ReadNyxChannelBotAsync(accessToken, nyxChannelBotId, ct);
        if (bot is null)
            return ChannelRegistrationAdoptionResult.Failure("nyxid_channel_bot_unavailable");

        var existing = (await _queryPort.QueryAllSnapshotsAsync(ct))
            .Select(static snapshot => snapshot.Registration)
            .FirstOrDefault(registration => !registration.Tombstoned &&
                string.Equals(registration.NyxChannelBotId, nyxChannelBotId, StringComparison.Ordinal));
        if (existing is not null)
            return ChannelRegistrationAdoptionResult.Failure("channel_bot_already_bound", existing.Id);

        var validation = ChannelBotRuntimeConfigValidation.ValidateStructure(request.RuntimeConfig);
        if (validation.Count > 0)
            return ChannelRegistrationAdoptionResult.Failure("invalid_runtime_config");

        var owner = await _ownerResolver.ResolveAsync(accessToken, scopeId, ct);
        if (!owner.Succeeded)
            return ChannelRegistrationAdoptionResult.Failure(owner.ErrorCode);

        var verifiedSelection = await VerifyRegistrationServicesAsync(
            accessToken,
            owner.Owner!,
            serviceSelection,
            ct);
        if (!verifiedSelection.Succeeded)
            return ChannelRegistrationAdoptionResult.Failure(verifiedSelection.ErrorCode);

        validation = ChannelBotRuntimeConfigValidation.ValidateSelectorAuthorization(
            request.RuntimeConfig,
            serviceSelection.AuthorizationMode,
            verifiedSelection.ServiceSlugs);
        if (validation.Count > 0)
            return ChannelRegistrationAdoptionResult.Failure("invalid_runtime_config");

        var registrationId = string.IsNullOrWhiteSpace(request.RegistrationId)
            ? Guid.NewGuid().ToString("N")
            : request.RegistrationId.Trim();
        var existingRegistration = await _queryPort.GetSnapshotAsync(registrationId, ct);
        if (existingRegistration is not null && !existingRegistration.Registration.Tombstoned)
            return ChannelRegistrationAdoptionResult.Failure("registration_id_already_exists");

        var relayCallbackUrl = NyxRelayCallbackUrl.Build(webhookBaseUrl);
        ChannelAgentKeyCredential? channelAgentKey = null;
        ConversationRouteBindingResult? routeBinding = null;
        try
        {
            channelAgentKey = await _agentKeyProvisioning.ProvisionAsync(
                bot.Platform,
                accessToken,
                relayCallbackUrl,
                scopeId,
                registrationId,
                owner.Owner!,
                ct);
            if (verifiedSelection.Plan is not null)
                channelAgentKey = await UpdateAgentKeyGrantAsync(accessToken, channelAgentKey, verifiedSelection.Plan, ct);

            routeBinding = await BindConversationRouteAsync(
                accessToken,
                bot.Id,
                request.NyxConversationRouteId,
                channelAgentKey.ApiKeyId,
                ct);

            var cmd = new ChannelBotRegisterCommand
            {
                RequestedId = registrationId,
                Platform = bot.Platform,
                NyxProviderSlug = string.IsNullOrWhiteSpace(request.NyxProviderSlug)
                    ? ResolveDefaultProviderSlug(bot.Platform)
                    : request.NyxProviderSlug.Trim(),
                ScopeId = scopeId,
                NyxAgentApiKeyId = channelAgentKey.ApiKeyId,
                NyxChannelBotId = bot.Id,
                NyxConversationRouteId = routeBinding.RouteId,
                WebhookUrl = bot.WebhookUrl,
                WorkflowResultDeliveryCredential = channelAgentKey.SecretReference.Clone(),
                ChannelAgentKey = channelAgentKey.Clone(),
                AuthorizationMode = serviceSelection.AuthorizationMode,
                DefaultSkillName = NormalizeDefaultSkillName(request.RuntimeConfig?.DefaultSkill?.Name),
                RuntimeConfig = ChannelRegistrationLocalMirrorRuntimeConfig.Build(
                    request.RuntimeConfig,
                    null,
                    null),
            };
            if (serviceSelection.AuthorizationMode == ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)
            {
                cmd.RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist();
                cmd.RegistrationServiceAllowlist.ServiceIds.Add(serviceSelection.ServiceIds);
            }
            if (!ChannelRegistrationAuthorizationContract.IsValidNewCommand(cmd))
                throw new InvalidOperationException("channel_authorization_contract_invalid");

            var receipt = await _commandFacade.RegisterLocalMirrorAsync(cmd, ct);
            return ChannelRegistrationAdoptionResult.Accepted(
                receipt,
                registrationId,
                bot.Platform,
                cmd.NyxProviderSlug,
                bot.Id,
                channelAgentKey.ApiKeyId,
                routeBinding.RouteId,
                relayCallbackUrl,
                bot.WebhookUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failureReason = NyxApiResponseHelper.SanitizeFailureReason(ex);
            _logger.LogWarning(
                "Nyx channel bot adoption failed: registration={RegistrationId}, botId={ChannelBotId}, routeId={RouteId}, apiKeyId={ApiKeyId}, failureCode={FailureCode}, failureType={FailureType}",
                registrationId,
                bot.Id,
                routeBinding?.RouteId,
                channelAgentKey?.ApiKeyId,
                failureReason,
                ex.GetType().Name);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (routeBinding is not null)
                await RollbackConversationRouteAsync(accessToken, routeBinding, cleanup.Token);
            if (channelAgentKey is not null)
                await _agentKeyProvisioning.CleanupAsync(accessToken, channelAgentKey, registrationId, cleanup.Token);

            return ChannelRegistrationAdoptionResult.Failure(failureReason);
        }
    }

    private async Task<VerifiedRegistrationServices> VerifyRegistrationServicesAsync(
        string accessToken,
        VerifiedChannelRegistrationOwner owner,
        ChannelRegistrationServiceSelection serviceSelection,
        CancellationToken ct)
    {
        if (serviceSelection.AuthorizationMode != ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)
            return new VerifiedRegistrationServices(true, string.Empty, [], null);

        var verified = await _authorizationPlanner.VerifySelectionAsync(new(
            accessToken,
            owner,
            serviceSelection.ServiceIds,
            []), ct);
        if (verified.Selection is null)
            return new VerifiedRegistrationServices(false, verified.ErrorCode, [], null);

        var planned = await _authorizationPlanner.PlanAsync(verified.Selection, [], ct);
        if (!planned.Succeeded)
            return new VerifiedRegistrationServices(false, planned.ErrorCode, [], null);

        return new VerifiedRegistrationServices(
            true,
            string.Empty,
            verified.Selection.RegistrationServices
                .Select(static service => service.Slug)
                .Where(static slug => !string.IsNullOrWhiteSpace(slug))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            planned.Plan);
    }

    private async Task<ChannelAgentKeyCredential> UpdateAgentKeyGrantAsync(
        string accessToken,
        ChannelAgentKeyCredential credential,
        VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan plan,
        CancellationToken ct)
    {
        var response = await _nyxClient.UpdateApiKeyAsync(
            accessToken,
            credential.ApiKeyId,
            JsonSerializer.Serialize(new
            {
                allowed_service_ids = plan.AllowedServiceIds.ToArray(),
                allowed_node_ids = plan.AllowedNodeIds.ToArray(),
                allow_all_services = false,
                allow_all_nodes = false,
            }),
            ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw new InvalidOperationException($"nyx_agent_key_update_failed {NyxApiResponseHelper.ExtractErrorDetail(response)}");

        var updated = credential.Clone();
        updated.Grant = new ChannelAgentKeyGrantSnapshot
        {
            AllowAllServices = false,
            AllowAllNodes = false,
            ScopePlanDigest = plan.ScopePlanDigest,
        };
        updated.Grant.AllowedServiceIds.Add(plan.AllowedServiceIds);
        updated.Grant.AllowedNodeIds.Add(plan.AllowedNodeIds);
        return updated;
    }

    private async Task<NyxChannelBotRecord?> ReadNyxChannelBotAsync(
        string accessToken,
        string nyxChannelBotId,
        CancellationToken ct)
    {
        try
        {
            var response = await _nyxClient.GetChannelBotAsync(accessToken, nyxChannelBotId, ct);
            return ParseNyxChannelBot(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Nyx channel bot read failed: botId={ChannelBotId}, failureType={FailureType}",
                nyxChannelBotId,
                ex.GetType().Name);
            return null;
        }
    }

    private async Task<ConversationRouteBindingResult> BindConversationRouteAsync(
        string accessToken,
        string nyxChannelBotId,
        string? requestedRouteId,
        string apiKeyId,
        CancellationToken ct)
    {
        NyxConversationRouteRecord? route = null;
        if (!string.IsNullOrWhiteSpace(requestedRouteId))
        {
            var routesResponse = await _nyxClient.ListConversationRoutesAsync(accessToken, nyxChannelBotId, ct);
            var routes = ParseNyxConversationRoutes(routesResponse);
            route = routes.FirstOrDefault(item => string.Equals(item.Id, requestedRouteId.Trim(), StringComparison.Ordinal));
            if (route is null)
                throw new InvalidOperationException("channel_route_not_found");
        }
        else
        {
            var routesResponse = await _nyxClient.ListConversationRoutesAsync(accessToken, nyxChannelBotId, ct);
            var routes = ParseNyxConversationRoutes(routesResponse)
                .Where(route => string.Equals(route.ChannelBotId, nyxChannelBotId, StringComparison.Ordinal))
                .ToArray();
            route = routes.FirstOrDefault(static item => item.DefaultAgent) ??
                (routes.Length == 1 ? routes[0] : null);
            if (route is null && routes.Length > 1)
                throw new InvalidOperationException("ambiguous_channel_bot_route");
        }

        if (route is null)
        {
            var createResponse = await _nyxClient.CreateConversationRouteAsync(
                accessToken,
                JsonSerializer.Serialize(new
                {
                    channel_bot_id = nyxChannelBotId,
                    agent_api_key_id = apiKeyId,
                    default_agent = true,
                }),
                ct);
            return new ConversationRouteBindingResult(
                NyxApiResponseHelper.ExtractRequiredId(createResponse, "channel_route_id"),
                Created: true,
                PreviousAgentApiKeyId: null,
                PreviousDefaultAgent: null);
        }

        if (string.Equals(route.AgentApiKeyId, apiKeyId, StringComparison.Ordinal) && route.DefaultAgent)
        {
            return new ConversationRouteBindingResult(
                route.Id,
                Created: false,
                PreviousAgentApiKeyId: null,
                PreviousDefaultAgent: null);
        }

        var updateResponse = await _nyxClient.UpdateConversationRouteAsync(
            accessToken,
            route.Id,
            JsonSerializer.Serialize(new
            {
                agent_api_key_id = apiKeyId,
                default_agent = true,
            }),
            ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(updateResponse))
            throw new InvalidOperationException($"channel_route_update_failed {NyxApiResponseHelper.ExtractErrorDetail(updateResponse)}");
        return new ConversationRouteBindingResult(
            ParseNyxConversationRoute(updateResponse)?.Id ?? route.Id,
            Created: false,
            PreviousAgentApiKeyId: route.AgentApiKeyId,
            PreviousDefaultAgent: route.DefaultAgent);
    }

    private async Task RollbackConversationRouteAsync(
        string accessToken,
        ConversationRouteBindingResult routeBinding,
        CancellationToken ct)
    {
        if (routeBinding.Created)
        {
            await NyxApiResponseHelper.TryRollbackAsync(
                () => _nyxClient.DeleteConversationRouteAsync(accessToken, routeBinding.RouteId, ct),
                "channel_route",
                routeBinding.RouteId,
                _logger);
            return;
        }

        if (string.IsNullOrWhiteSpace(routeBinding.PreviousAgentApiKeyId) ||
            routeBinding.PreviousDefaultAgent is null)
        {
            return;
        }

        await NyxApiResponseHelper.TryRollbackAsync(
            () => _nyxClient.UpdateConversationRouteAsync(
                accessToken,
                routeBinding.RouteId,
                JsonSerializer.Serialize(new
                {
                    agent_api_key_id = routeBinding.PreviousAgentApiKeyId,
                    default_agent = routeBinding.PreviousDefaultAgent.Value,
                }),
                ct),
            "channel_route",
            routeBinding.RouteId,
            _logger);
    }

    private static NyxChannelBotRecord? ParseNyxChannelBot(string response)
    {
        using var document = JsonDocument.Parse(response);
        var root = UnwrapObject(document.RootElement, ["data", "channel_bot", "bot"]);
        return ParseNyxChannelBotElement(root);
    }

    private static NyxChannelBotRecord? ParseNyxChannelBotElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadNonEmptyString(element, "id") ?? ReadNonEmptyString(element, "bot_id");
        var platform = ReadNonEmptyString(element, "platform") ?? ReadNonEmptyString(element, "channel");
        if (id is null || platform is null)
            return null;

        var label = ReadNonEmptyString(element, "label") ??
            ReadNonEmptyString(element, "name") ??
            ReadNonEmptyString(element, "display_name") ??
            id;
        var status = ReadNonEmptyString(element, "status") ??
            (ReadBoolean(element, "active") == true ? "active" : "unknown");
        return new NyxChannelBotRecord(
            id,
            platform.Trim().ToLowerInvariant(),
            label,
            status,
            ReadBoolean(element, "active") ?? !string.Equals(status, "disabled", StringComparison.OrdinalIgnoreCase),
            ReadNonEmptyString(element, "webhook_url") ?? ReadNonEmptyString(element, "callback_url") ?? string.Empty);
    }

    private static IReadOnlyList<NyxConversationRouteRecord> ParseNyxConversationRoutes(string response)
    {
        using var document = JsonDocument.Parse(response);
        return TryGetArray(document.RootElement, ["data", "routes", "items", "channel_conversations", "channelConversations"], out var array)
            ? array.EnumerateArray().Select(ParseNyxConversationRoute).Where(static route => route is not null).Select(static route => route!).ToArray()
            : [];
    }

    private static NyxConversationRouteRecord? ParseNyxConversationRoute(string response)
    {
        using var document = JsonDocument.Parse(response);
        return ParseNyxConversationRoute(UnwrapObject(document.RootElement, ["data", "route", "channel_conversation"]));
    }

    private static NyxConversationRouteRecord? ParseNyxConversationRoute(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadNonEmptyString(element, "id") ?? ReadNonEmptyString(element, "route_id");
        if (id is null)
            return null;

        return new NyxConversationRouteRecord(
            id,
            ReadNonEmptyString(element, "channel_bot_id") ??
                ReadNonEmptyString(element, "bot_id") ??
                ReadNonEmptyString(element, "nyx_channel_bot_id") ??
                string.Empty,
            ReadNonEmptyString(element, "agent_api_key_id") ?? string.Empty,
            ReadBoolean(element, "default_agent") ?? ReadBoolean(element, "is_default") ?? false);
    }

    private static bool TryGetArray(JsonElement root, IReadOnlyList<string> propertyNames, out JsonElement array)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Array)
                {
                    array = element;
                    return true;
                }
            }
        }

        array = default;
        return false;
    }

    private static JsonElement UnwrapObject(JsonElement root, IReadOnlyList<string> propertyNames)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return root;

        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Object)
                return element;
        }

        return root;
    }

    private static string? ReadNonEmptyString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        var value = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? ReadBoolean(JsonElement element, string propertyName)
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

    private static string NormalizeDefaultSkillName(string? value) =>
        (value ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();

    private static string ResolveDefaultProviderSlug(string platform) =>
        $"api-{platform}-bot";

    private sealed record NyxChannelBotRecord(
        string Id,
        string Platform,
        string Label,
        string Status,
        bool Active,
        string WebhookUrl);

    private sealed record NyxConversationRouteRecord(
        string Id,
        string ChannelBotId,
        string AgentApiKeyId,
        bool DefaultAgent);

    private sealed record ConversationRouteBindingResult(
        string RouteId,
        bool Created,
        string? PreviousAgentApiKeyId,
        bool? PreviousDefaultAgent);

    private sealed record VerifiedRegistrationServices(
        bool Succeeded,
        string ErrorCode,
        IReadOnlyList<string> ServiceSlugs,
        VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan? Plan);
}
