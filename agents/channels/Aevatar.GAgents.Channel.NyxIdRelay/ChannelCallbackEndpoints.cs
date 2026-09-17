using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VerifiedChannelRegistrationAuthorizationPlan = Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public static class ChannelCallbackEndpoints
{
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=public rebuild surfaces, new=internal Runtime startup helper only
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=/registrations/rebuild HTTP surface, new=no public rebuild route
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=manual projection refresh endpoint, new=startup-owned projection refresh
    // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
    //   Old pattern: Nyx relay registration endpoints + singleton provisioning services 在 Host 内做 platform selection / scope resolution / remote Nyx provisioning / actor creation / envelope construction / dispatch through raw runtime/dispatch helpers。
    //   New principle: Channel registration 暴露 typed application command facade(reuse existing CQRS command dispatch skeleton);Host 仅 adapt HTTP;provisioning adapters 只调 existing NyxID REST surfaces(**不修改 NyxID 仓库**);local mirror writes 进 standard command skeleton via narrow dispatch port。**不引入新 actor type / 新 envelope / 新 projection phase**(reflector force-pick minimal,排除 structural 的 ChannelRelayRegistrationRunGAgent)。
    // Refactor (iter36/cluster-042-channel-diagnostics-readmodel):
    //   Old pattern: Channel runtime diagnostics 用 singleton in-memory list with retention trimming;diagnostics endpoint 直接读 process-local list。
    //   New principle: Channel diagnostics 改为 logs/metrics only(observability path)OR actor/projection-backed diagnostic events with readmodel query。**禁止** public endpoint 读 singleton process memory 作 diagnostic fact source。
    public static IEndpointRouteBuilder MapChannelCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channels").WithTags("ChannelRuntime");

        // Registration CRUD — requires authentication
        group.MapGet("/me", HandleGetCallerInfoAsync).RequireAuthorization();
        group.MapGet("/services", HandleListServicesAsync)
            .Produces<object[]>(StatusCodes.Status200OK, "application/json")
            .RequireAuthorization();
        group.MapPost("/registrations", HandleRegisterAsync)
            .WithEndpointAudit(
                "channel.registration.create",
                AuditSensitivityLevel.Confidential,
                "channel-registration",
                EndpointAuditTargetResolvers.Static("channel-registration", "new"),
                ChannelRegistrationRequestSummary)
            .RequireAuthorization();
        group.MapGet("/registrations", HandleListRegistrationsAsync)
            .Produces<object[]>(StatusCodes.Status200OK, "application/json")
            .RequireAuthorization();
        group.MapGet("/registrations/{registrationId}", HandleGetRegistrationAsync)
            .Produces<object>(StatusCodes.Status200OK, "application/json")
            .RequireAuthorization();
        group.MapPost("/registrations/{registrationId}", HandleUpdateRegistrationAsync)
            .WithEndpointAudit(
                "channel.registration.update",
                AuditSensitivityLevel.Confidential,
                "channel-registration",
                EndpointAuditTargetResolvers.FromRouteValue("channel-registration", "registrationId"),
                EndpointAuditSanitizers.WithRouteValues("registrationId"))
            .RequireAuthorization();
        group.MapGet("/registrations/{registrationId}/status", HandleGetStatusAsync).RequireAuthorization();
        group.MapPost(
                "/registrations/{registrationId}/workflow-result-delivery/repair",
                HandleRepairWorkflowResultDeliveryAsync)
            .WithEndpointAudit(
                "channel.registration.workflow-result-delivery.repair",
                AuditSensitivityLevel.Confidential,
                "channel-registration",
                EndpointAuditTargetResolvers.FromRouteValue("channel-registration", "registrationId"),
                EndpointAuditSanitizers.WithRouteValues("registrationId"))
            .RequireAuthorization();
        group.MapDelete("/registrations/{registrationId}", HandleDeleteRegistrationAsync)
            .WithEndpointAudit(
                "channel.registration.delete",
                AuditSensitivityLevel.Confidential,
                "channel-registration",
                EndpointAuditTargetResolvers.FromRouteValue("channel-registration", "registrationId"),
                EndpointAuditSanitizers.WithRouteValues("registrationId"))
            .RequireAuthorization();

        // Diagnostic: test reply path without going through full LLM chat
        group.MapPost("/registrations/{registrationId}/test-reply", HandleTestReplyAsync)
            .WithEndpointAudit(
                "channel.registration.test-reply",
                AuditSensitivityLevel.Confidential,
                "channel-registration",
                EndpointAuditTargetResolvers.FromRouteValue("channel-registration", "registrationId"),
                EndpointAuditSanitizers.WithRouteValues("registrationId"))
            .RequireAuthorization();
        group.MapGet("/diagnostics/errors", HandleGetDiagnosticErrorsAsync).RequireAuthorization();

        return app;
    }

    private static ValueTask<string> ChannelRegistrationRequestSummary(EndpointAuditSanitizationContext context)
    {
        return ValueTask.FromResult(
            $"{context.HttpContext.Request.Method} {EndpointAuditSanitizers.ResolveRoutePattern(context.HttpContext)}");
    }

    // ─── Registration CRUD ───

    private static readonly JsonSerializerOptions RegistrationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext http,
        [FromServices] ChannelRegistrationAdoptionFacade adoptionFacade,
        [FromServices] NyxIdRelayOptions relayOptions,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Registration");

        RegistrationRequest? request;
        ChannelRegistrationServiceSelection serviceSelection;
        try
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
            if (!ChannelRegistrationServiceIdsJsonParser.TryParse(
                    document.RootElement,
                    out serviceSelection))
            {
                return Results.BadRequest(new { error = "invalid_service_ids" });
            }

            if (!ChannelBotRuntimeConfigJsonParser.TryParse(
                    document.RootElement,
                    out var runtimeConfig))
            {
                return Results.BadRequest(new { error = "invalid_runtime_config" });
            }

            var parsedRequest = document.RootElement.Deserialize<RegistrationRequest>(RegistrationJsonOptions);
            request = parsedRequest is null
                ? null
                : parsedRequest with { RuntimeConfig = runtimeConfig };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid registration request payload");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.NyxChannelBotId))
            return Results.BadRequest(new { error = "nyx_channel_bot_id is required" });

        var accessToken = ResolveBearerAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Results.Json(
                new { error = "missing_access_token" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var webhookBaseUrl = ResolveWebhookBaseUrl(http, relayOptions.WebhookBaseUrl);
        if (!NyxRelayCallbackUrl.IsSecureBaseUrl(webhookBaseUrl))
            return Results.BadRequest(new { error = "insecure_webhook_base_url" });

        var scopeResolution = ResolveRegistrationOwnerScopeId(http, null);
        if (scopeResolution.Error is not null)
            return Results.BadRequest(new { error = scopeResolution.Error });

        var result = await adoptionFacade.AdoptAsync(
            new ChannelRegistrationAdoptionRequest(
                request.RegistrationId,
                request.NyxChannelBotId,
                request.NyxConversationRouteId,
                request.NyxProviderSlug,
                request.RuntimeConfig),
            serviceSelection,
            accessToken,
            scopeResolution.ScopeId!,
            webhookBaseUrl,
            ct);
        if (!result.Succeeded)
        {
            var payload = string.Equals(result.ErrorCode, "channel_bot_already_bound", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(result.ExistingRegistrationId)
                    ? new { status = "error", error = result.ErrorCode, registration_id = result.ExistingRegistrationId }
                    : (object)new { status = "error", error = result.ErrorCode };
            return Results.Json(
                payload,
                RegistrationJsonOptions,
                statusCode: ResolveProvisioningFailureStatusCode(result.ErrorCode));
        }

        return Results.Json(
            new
            {
                status = "accepted",
                registration_id = result.RegistrationId,
                command_id = result.Receipt!.CommandId,
                correlation_id = result.Receipt.CorrelationId,
                platform = result.Platform,
                nyx_provider_slug = result.NyxProviderSlug,
                nyx_channel_bot_id = result.NyxChannelBotId,
                nyx_agent_api_key_id = result.NyxAgentApiKeyId,
                nyx_conversation_route_id = result.NyxConversationRouteId,
                relay_callback_url = result.RelayCallbackUrl,
                webhook_url = result.WebhookUrl,
                workflow_result_delivery_status = "registration_pending",
            },
            RegistrationJsonOptions,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<VerifiedRegistrationServices> VerifyRegistrationServicesAsync(
        ChannelRegistrationAuthorizationPlanner authorizationPlanner,
        string accessToken,
        VerifiedChannelRegistrationOwner owner,
        ChannelRegistrationServiceSelection serviceSelection,
        CancellationToken ct)
    {
        if (serviceSelection.AuthorizationMode != ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)
            return new VerifiedRegistrationServices(true, string.Empty, [], null);

        var verified = await authorizationPlanner.VerifySelectionAsync(new(
            accessToken,
            owner,
            serviceSelection.ServiceIds,
            []), ct);
        if (verified.Selection is null)
            return new VerifiedRegistrationServices(false, verified.ErrorCode, [], null);

        var planned = await authorizationPlanner.PlanAsync(verified.Selection, [], ct);
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

    private static Task<ChannelAgentKeyCredential> UpdateAgentKeyGrantForSelectionAsync(
        NyxIdApiClient nyxClient,
        string accessToken,
        ChannelAgentKeyCredential credential,
        ChannelRegistrationServiceSelection serviceSelection,
        VerifiedChannelRegistrationAuthorizationPlan? plan,
        CancellationToken ct) =>
        serviceSelection.AuthorizationMode == ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist
            ? UpdateAgentKeyGrantAsync(
                nyxClient,
                accessToken,
                credential,
                plan ?? throw new InvalidOperationException("nyxid_scope_plan_unavailable"),
                ct)
            : UpdateAgentKeyDefaultGrantAsync(nyxClient, accessToken, credential, ct);

    private static async Task<ChannelAgentKeyCredential> UpdateAgentKeyDefaultGrantAsync(
        NyxIdApiClient nyxClient,
        string accessToken,
        ChannelAgentKeyCredential credential,
        CancellationToken ct)
    {
        var response = await nyxClient.UpdateApiKeyAsync(
            accessToken,
            credential.ApiKeyId,
            JsonSerializer.Serialize(new
            {
                allowed_service_ids = Array.Empty<string>(),
                allowed_node_ids = Array.Empty<string>(),
                allow_all_services = true,
                allow_all_nodes = true,
            }),
            ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw new InvalidOperationException($"api_key_update_failed {NyxApiResponseHelper.ExtractErrorDetail(response)}");

        var updated = credential.Clone();
        updated.Grant = new ChannelAgentKeyGrantSnapshot
        {
            AllowAllServices = true,
            AllowAllNodes = true,
        };
        return updated;
    }

    private static async Task<ChannelAgentKeyCredential> UpdateAgentKeyGrantAsync(
        NyxIdApiClient nyxClient,
        string accessToken,
        ChannelAgentKeyCredential credential,
        VerifiedChannelRegistrationAuthorizationPlan plan,
        CancellationToken ct)
    {
        var response = await nyxClient.UpdateApiKeyAsync(
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
            throw new InvalidOperationException($"api_key_update_failed {NyxApiResponseHelper.ExtractErrorDetail(response)}");

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

    private static async Task RestoreAgentKeyGrantAsync(
        NyxIdApiClient nyxClient,
        string accessToken,
        ChannelAgentKeyCredential credential,
        CancellationToken ct)
    {
        var grant = credential.Grant ?? new ChannelAgentKeyGrantSnapshot
        {
            AllowAllServices = true,
            AllowAllNodes = true,
        };
        var response = await nyxClient.UpdateApiKeyAsync(
            accessToken,
            credential.ApiKeyId,
            JsonSerializer.Serialize(new
            {
                allowed_service_ids = grant.AllowedServiceIds.ToArray(),
                allowed_node_ids = grant.AllowedNodeIds.ToArray(),
                allow_all_services = grant.AllowAllServices,
                allow_all_nodes = grant.AllowAllNodes,
            }),
            ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw new InvalidOperationException($"api_key_grant_restore_failed {NyxApiResponseHelper.ExtractErrorDetail(response)}");
    }

    private static async Task<NyxChannelBotRecord?> ReadNyxChannelBotAsync(
        NyxIdApiClient nyxClient,
        string accessToken,
        string nyxChannelBotId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var response = await nyxClient.GetChannelBotAsync(accessToken, nyxChannelBotId, ct);
            return ParseNyxChannelBot(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Nyx channel bot read failed: botId={ChannelBotId}, failureType={FailureType}",
                nyxChannelBotId,
                ex.GetType().Name);
            return null;
        }
    }

    private static async Task<ConversationRouteBindingResult> BindConversationRouteAsync(
        NyxIdApiClient nyxClient,
        string accessToken,
        string nyxChannelBotId,
        string? requestedRouteId,
        string apiKeyId,
        CancellationToken ct)
    {
        var routesResponse = await nyxClient.ListConversationRoutesAsync(accessToken, nyxChannelBotId, ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(routesResponse))
            throw new InvalidOperationException($"channel_route_request_failed {NyxApiResponseHelper.ExtractErrorDetail(routesResponse)}");

        var routes = ParseNyxConversationRoutes(routesResponse)
            .Where(route => string.IsNullOrWhiteSpace(route.ChannelBotId) ||
                string.Equals(route.ChannelBotId, nyxChannelBotId, StringComparison.Ordinal))
            .ToArray();
        NyxConversationRouteRecord? route;
        if (!string.IsNullOrWhiteSpace(requestedRouteId))
        {
            var normalizedRouteId = requestedRouteId.Trim();
            route = routes.SingleOrDefault(candidate => string.Equals(candidate.Id, normalizedRouteId, StringComparison.Ordinal));
            if (route is null)
                throw new InvalidOperationException("channel_route_not_accessible");
        }
        else
        {
            var defaults = routes.Where(static candidate => candidate.DefaultAgent).ToArray();
            route = defaults.Length == 1
                ? defaults[0]
                : routes.Length == 1
                    ? routes[0]
                    : null;
            if (route is null && routes.Length > 1)
                throw new InvalidOperationException("ambiguous_channel_bot_route");
        }

        if (route is null)
        {
            var createResponse = await nyxClient.CreateConversationRouteAsync(
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

        var updateResponse = await nyxClient.UpdateConversationRouteAsync(
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

    private static async Task RollbackConversationRouteAsync(
        NyxIdApiClient nyxClient,
        string accessToken,
        ConversationRouteBindingResult routeBinding,
        ILogger logger,
        CancellationToken ct)
    {
        if (routeBinding.Created)
        {
            await NyxApiResponseHelper.TryRollbackAsync(
                () => nyxClient.DeleteConversationRouteAsync(accessToken, routeBinding.RouteId, ct),
                "channel_route",
                routeBinding.RouteId,
                logger);
            return;
        }

        if (string.IsNullOrWhiteSpace(routeBinding.PreviousAgentApiKeyId) ||
            routeBinding.PreviousDefaultAgent is null)
        {
            return;
        }

        await NyxApiResponseHelper.TryRollbackAsync(
            () => nyxClient.UpdateConversationRouteAsync(
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
            logger);
    }

    private static async Task<IResult> HandleListServicesAsync(
        HttpContext http,
        [FromServices] IChannelRegistrationNyxIdAuthorizationPort authorizationPort,
        CancellationToken ct)
    {
        var accessToken = ResolveBearerAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();

        var services = await authorizationPort.ReadUserServicesAsync(accessToken, ct);
        if (!services.Succeeded)
        {
            var errorCode = services.Failure?.Code ?? "nyxid_user_services_unavailable";
            return Results.Json(
                new { error = errorCode },
                statusCode: ResolveProvisioningFailureStatusCode(errorCode));
        }

        return Results.Json(
            services.Value!.Services.Select(static service => new
            {
                id = service.Id,
                slug = service.Slug,
                label = service.Label ?? service.CatalogServiceName ?? service.Slug,
                catalog_service_name = service.CatalogServiceName ?? string.Empty,
                active = service.IsActive,
                credential_source = MapCredentialSource(service.CredentialSource),
            }).ToArray(),
            RegistrationJsonOptions);
    }

    /// <summary>
    /// Lists channel-bot registrations scoped to the caller's own account. NyxID channel-bot
    /// visibility is owner-scoped, so this endpoint does not expose a cross-account admin view.
    /// </summary>
    private static async Task<IResult> HandleListRegistrationsAsync(
        HttpContext http,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] NyxIdApiClient nyxClient,
        CancellationToken ct)
    {
        var accessToken = ResolveBearerAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();

        var callerScope = ResolveScopeId(http, null, required: false).ScopeId;

        var botResponse = await nyxClient.ListChannelBotsAsync(accessToken, ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(botResponse))
        {
            return Results.Json(
                new { error = "nyxid_channel_bots_unavailable", detail = NyxApiResponseHelper.ExtractErrorDetail(botResponse) },
                RegistrationJsonOptions,
                statusCode: StatusCodes.Status502BadGateway);
        }

        var bots = ParseNyxChannelBots(botResponse);
        var snapshots = await queryPort.QueryAllSnapshotsAsync(ct);
        var localByBotId = snapshots
            .Where(snapshot => string.Equals(
                snapshot.Registration.ScopeId,
                callerScope,
                StringComparison.Ordinal))
            .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.Registration.NyxChannelBotId))
            .GroupBy(static snapshot => snapshot.Registration.NyxChannelBotId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(item => item.StateVersion).First(), StringComparer.Ordinal);

        var result = bots.Select(bot =>
        {
            localByBotId.TryGetValue(bot.Id, out var snapshot);
            return MapRegistrationListRow(bot, snapshot, callerScope);
        }).ToArray();

        return Results.Json(result, RegistrationJsonOptions);
    }

    private static async Task<IResult> HandleGetRegistrationAsync(
        string registrationId,
        HttpContext http,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        CancellationToken ct)
    {
        var snapshot = await queryPort.GetSnapshotAsync(registrationId, ct);
        if (snapshot is null)
            return Results.NotFound(new { error = "Registration not found" });

        if (!CallerOwnsRegistration(http, snapshot.Registration))
            return Results.NotFound(new { error = "Registration not found" });

        return Results.Json(
            MapRegistrationDetail(snapshot),
            RegistrationJsonOptions);
    }

    private static async Task<IResult> HandleUpdateRegistrationAsync(
        string registrationId,
        HttpContext http,
        [FromServices] ChannelRegistrationCommandFacade commandFacade,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] IChannelRegistrationOwnerResolver ownerResolver,
        [FromServices] ChannelRegistrationAuthorizationPlanner authorizationPlanner,
        [FromServices] NyxIdApiClient nyxClient,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Registration");
        ChannelBotRuntimeConfig? runtimeConfig;
        ChannelRegistrationServiceSelection serviceSelection;
        try
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
            if (!ChannelRegistrationServiceIdsJsonParser.TryParse(
                    document.RootElement,
                    out serviceSelection))
            {
                return Results.BadRequest(new { error = "invalid_service_ids" });
            }

            if (!ChannelBotRuntimeConfigJsonParser.TryParse(
                    document.RootElement,
                    out runtimeConfig))
            {
                return Results.BadRequest(new { error = "invalid_runtime_config" });
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid runtime config update payload");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }

        var registration = await queryPort.GetAsync(registrationId, ct);
        if (registration is null)
            return Results.NotFound(new { error = "Registration not found" });

        if (!CallerOwnsRegistration(http, registration))
        {
            return Results.NotFound(new { error = "Registration not found" });
        }

        var effectiveServiceSelection = serviceSelection.Specified
            ? serviceSelection
            : CurrentServiceSelection(registration);
        var validation = ChannelBotRuntimeConfigValidation.ValidateStructure(runtimeConfig);
        if (validation.Count > 0)
        {
            return Results.Json(
                new
                {
                    error = "invalid_runtime_config",
                    field_errors = validation,
                },
                RegistrationJsonOptions,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var accessToken = ResolveBearerAccessToken(http);
        VerifiedRegistrationServices verifiedServices = new(true, string.Empty, [], null);
        if (effectiveServiceSelection.AuthorizationMode == ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist ||
            serviceSelection.Specified)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                return Results.Unauthorized();

            var owner = await ownerResolver.ResolveAsync(accessToken, registration.ScopeId, ct);
            if (!owner.Succeeded)
                return Results.Json(
                    new { error = owner.ErrorCode },
                    statusCode: ResolveProvisioningFailureStatusCode(owner.ErrorCode));

            verifiedServices = await VerifyRegistrationServicesAsync(
                authorizationPlanner,
                accessToken,
                owner.Owner!,
                effectiveServiceSelection,
                ct);
            if (!verifiedServices.Succeeded)
                return Results.Json(
                    new { error = verifiedServices.ErrorCode },
                    statusCode: ResolveProvisioningFailureStatusCode(verifiedServices.ErrorCode));
        }

        validation = ChannelBotRuntimeConfigValidation.ValidateSelectorAuthorization(
            runtimeConfig,
            effectiveServiceSelection.AuthorizationMode,
            verifiedServices.ServiceSlugs);
        if (validation.Count > 0)
        {
            return Results.Json(
                new
                {
                    error = "invalid_runtime_config",
                    field_errors = validation,
                },
                RegistrationJsonOptions,
                statusCode: StatusCodes.Status400BadRequest);
        }

        runtimeConfig = ApplyServiceSelectionDefaults(
            runtimeConfig,
            serviceSelection,
            verifiedServices.ServiceSlugs);

        ChannelAgentKeyCredential? updatedAgentKey = null;
        if (serviceSelection.Specified)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                return Results.Unauthorized();
            if (registration.ChannelAgentKey is null)
            {
                return Results.Json(
                    new { error = "channel_agent_key_unavailable" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            updatedAgentKey = await UpdateAgentKeyGrantForSelectionAsync(
                nyxClient,
                accessToken,
                registration.ChannelAgentKey,
                effectiveServiceSelection,
                verifiedServices.Plan,
                ct);
        }

        ChannelRegistrationCommandAcceptedReceipt receipt;
        try
        {
            receipt = await commandFacade.UpdateRuntimeConfigAsync(
                registrationId,
                runtimeConfig,
                runtimeConfig?.DefaultSkill?.Name ?? string.Empty,
                effectiveServiceSelection,
                updatedAgentKey,
                ct);
        }
        catch (Exception ex) when (updatedAgentKey is not null)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await RestoreAgentKeyGrantAsync(nyxClient, accessToken!, registration.ChannelAgentKey!, cleanup.Token);
            }
            catch (Exception restoreEx)
            {
                logger.LogWarning(
                    "Nyx agent key grant restore failed after runtime config dispatch failure: registration={RegistrationId}, apiKeyId={ApiKeyId}, failureType={FailureType}, restoreFailureType={RestoreFailureType}",
                    registrationId,
                    registration.ChannelAgentKey!.ApiKeyId,
                    ex.GetType().Name,
                    restoreEx.GetType().Name);
            }
            throw;
        }

        return Results.Accepted(value: new
        {
            status = "accepted",
            registration_id = registrationId,
            command_id = receipt.CommandId,
            correlation_id = receipt.CorrelationId,
            skill_name = runtimeConfig?.DefaultSkill?.Name ?? string.Empty,
        });
    }

    private static ChannelBotRuntimeConfig? ApplyServiceSelectionDefaults(
        ChannelBotRuntimeConfig? runtimeConfig,
        ChannelRegistrationServiceSelection serviceSelection,
        IReadOnlyList<string> verifiedServiceSlugs)
    {
        if (!serviceSelection.Specified ||
            serviceSelection.AuthorizationMode != ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist ||
            verifiedServiceSlugs.Count == 0)
        {
            return runtimeConfig;
        }

        var normalized = runtimeConfig?.Clone() ?? new ChannelBotRuntimeConfig();
        if (normalized.CredentialSourceMode == ChannelBotRuntimeCredentialSourceMode.Unspecified)
            normalized.CredentialSourceMode = ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey;
        if (!normalized.ToolSetRefs.Contains(ToolSetNames.ChannelReplyDefault))
            normalized.ToolSetRefs.Add(ToolSetNames.ChannelReplyDefault);

        if (!HasNyxIdServiceSelectors(normalized))
        {
            foreach (var slug in verifiedServiceSlugs)
            {
                if (string.IsNullOrWhiteSpace(slug))
                    continue;

                normalized.NyxidServiceSelectors.Add(new ChannelBotRuntimeNyxIdServiceSelector
                {
                    ServiceSlug = slug,
                });
            }
        }

        return normalized;
    }

    private static bool HasNyxIdServiceSelectors(ChannelBotRuntimeConfig? runtimeConfig) =>
        runtimeConfig?.NyxidServiceSelectors.Any(static selector => !string.IsNullOrWhiteSpace(selector.ServiceSlug)) == true;

    private static async Task<IResult> HandleRepairWorkflowResultDeliveryAsync(
        string registrationId,
        HttpContext http,
        [FromServices] IChannelWorkflowResultDeliveryRepairService repairService,
        CancellationToken ct)
    {
        var accessToken = ResolveBearerAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();

        var callerScopeId = ResolveScopeId(http, null, required: false).ScopeId;
        var requestedBySubjectId = ResolveSubjectId(http.User);
        if (string.IsNullOrWhiteSpace(callerScopeId) ||
            string.IsNullOrWhiteSpace(requestedBySubjectId))
        {
            return Results.NotFound(new { error = "Registration not found" });
        }

        var result = await repairService.RepairAsync(
            registrationId,
            callerScopeId,
            requestedBySubjectId,
            accessToken,
            ct);
        var repairFailed = result.Status ==
            ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed;
        var payload = new
        {
            status = MapRepairResultStatus(result.Status),
            repair_request_id = result.RequestId,
            registration_id = result.RegistrationId,
            nyx_agent_api_key_id = result.NyxAgentApiKeyId,
            workflow_result_delivery_status = MapRepairCapabilityStatus(result.Status),
            failure_phase = repairFailed ? MapRepairPhase(result.FailurePhase) : null,
            failure_reason = repairFailed ? MapRepairFailureReason(result.FailureReason) : null,
            note = MapRepairResultNote(result.Status),
        };
        return Results.Json(
            payload,
            RegistrationJsonOptions,
            statusCode: MapRepairResultStatusCode(result.Status));
    }

    /// <summary>
    /// Caller info for the page: own scope id + whether the caller has aevatar admin access
    /// (so the UI can offer the cross-account view).
    /// </summary>
    private static async Task<IResult> HandleGetCallerInfoAsync(
        HttpContext http,
        [FromServices] IPlatformAdminAuthorizer adminAuthorizer,
        CancellationToken ct)
    {
        var callerScope = ResolveScopeId(http, null, required: false).ScopeId ?? string.Empty;
        var token = ResolveBearerAccessToken(http);
        var caller = string.IsNullOrWhiteSpace(token)
            ? PlatformCaller.NotElevated
            : await adminAuthorizer.ResolveCallerAsync(token, ct);

        return Results.Json(new
        {
            scope_id = callerScope,
            is_admin = caller.IsElevated,
            role = caller.Role,
            grant_source = caller.GrantSource,
        });
    }

    /// <summary>
    /// Live bot status for the catalog badges and the verify-step lights. The facade
    /// list returns the registration record only; the live <c>active</c> /
    /// <c>pending_webhook</c> state lives on NyxID, so this reads it server-side via
    /// the existing channel-bot client (no browser→NyxID CORS, no NyxID change).
    /// Status read failures degrade to <c>unknown</c> — polling must never 500.
    /// </summary>
    /// <remarks>
    /// L1 (cross-tenant disclosure): a registration the caller does not own is
    /// indistinguishable from a non-existent one — the handler returns 404 rather
    /// than a populated degraded response, so an authenticated caller cannot probe
    /// another tenant's bot platform/last-activity by guessing registration ids.
    /// The one exception is a caller with aevatar admin access, who is allowed the
    /// cross-account view (mirrors <see cref="HandleListRegistrationsAsync"/>) and
    /// still only gets aevatar's own relay-activity observation, never the foreign
    /// owner's NyxID live status.
    /// </remarks>
    private static async Task<IResult> HandleGetStatusAsync(
        string registrationId,
        HttpContext http,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] NyxIdApiClient nyxClient,
        [FromServices] IPlatformAdminAuthorizer adminAuthorizer,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var registration = await queryPort.GetAsync(registrationId, ct);
        if (registration is null)
            return Results.NotFound(new { error = "Registration not found" });
        var capabilityStatus = ChannelWorkflowResultDeliveryCapability.Resolve(registration);
        var repairFailed = capabilityStatus ==
            ChannelWorkflowResultDeliveryCapabilityStatus.RepairFailed;
        var capabilityStatusValue = MapCapabilityStatus(registration, capabilityStatus);
        var failurePhaseValue = repairFailed
            ? MapRepairPhase(registration.WorkflowResultDeliveryRepair?.FailurePhase ??
                ChannelWorkflowResultDeliveryRepairPhase.Unspecified)
            : null;
        var failureReasonValue = repairFailed
            ? MapRepairFailureReason(registration.WorkflowResultDeliveryRepair?.FailureReason ??
                ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified)
            : null;

        // Cross-account bot: NyxID's channel-bot API is strictly owner-scoped, so we can't query its live status.
        // Instead report aevatar's OWN observation: the relay-activity read model marks a bot
        // active once it has received a verified inbound. No historical backfill exists, so a
        // bot that was active before this feature shipped shows pending until its next inbound.
        var callerScope = ResolveScopeId(http, null, required: false).ScopeId;
        if (!string.IsNullOrWhiteSpace(callerScope)
            && !string.Equals(registration.ScopeId, callerScope, StringComparison.Ordinal))
        {
            // L1: only aevatar admin access may see a foreign registration's status.
            // For any other caller a mismatched scope is a 404 (existence-hiding),
            // NOT a populated degraded response — otherwise the platform/activity of
            // another tenant's bot leaks to anyone who can guess a registration id.
            var token = ResolveBearerAccessToken(http);
            var caller = string.IsNullOrWhiteSpace(token)
                ? PlatformCaller.NotElevated
                : await adminAuthorizer.ResolveCallerAsync(token, ct);
            if (!caller.IsElevated)
                return Results.NotFound(new { error = "Registration not found" });

            var observedAt = registration.LastInboundAtUtc;
            return Results.Json(new
            {
                registration_id = registrationId,
                nyx_channel_bot_id = registration.NyxChannelBotId,
                status = observedAt is not null ? "active" : "pending_webhook",
                last_event_at = observedAt?.ToDateTimeOffset(),
                workflow_result_delivery_status = capabilityStatusValue,
                workflow_result_delivery_failure_phase = failurePhaseValue,
                workflow_result_delivery_failure_reason = failureReasonValue,
                owned = false,
            }, RegistrationJsonOptions);
        }

        var accessToken = ResolveBearerAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(registration.NyxChannelBotId))
        {
            return Results.Json(new
            {
                registration_id = registrationId,
                status = "unknown",
                workflow_result_delivery_status = capabilityStatusValue,
                workflow_result_delivery_failure_phase = failurePhaseValue,
                workflow_result_delivery_failure_reason = failureReasonValue,
                note = "no channel bot id",
            }, RegistrationJsonOptions);
        }

        string raw;
        try
        {
            raw = await nyxClient.GetChannelBotAsync(accessToken, registration.NyxChannelBotId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Status").LogWarning(
                ex,
                "Nyx channel-bot status read failed: registration={RegistrationId}, botId={BotId}",
                registrationId,
                registration.NyxChannelBotId);
            return Results.Json(new
            {
                registration_id = registrationId,
                nyx_channel_bot_id = registration.NyxChannelBotId,
                status = "unknown",
                workflow_result_delivery_status = capabilityStatusValue,
                workflow_result_delivery_failure_phase = failurePhaseValue,
                workflow_result_delivery_failure_reason = failureReasonValue,
                error = "status_query_failed",
            }, RegistrationJsonOptions);
        }

        var (status, lastEventAt) = ParseChannelBotStatus(raw);
        return Results.Json(new
        {
            registration_id = registrationId,
            nyx_channel_bot_id = registration.NyxChannelBotId,
            status,
            last_event_at = lastEventAt,
            workflow_result_delivery_status = capabilityStatusValue,
            workflow_result_delivery_failure_phase = failurePhaseValue,
            workflow_result_delivery_failure_reason = failureReasonValue,
        }, RegistrationJsonOptions);
    }

    private static (string Status, string? LastEventAt) ParseChannelBotStatus(string response)
    {
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            return ("unknown", null);

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            // NyxID may wrap the resource in { "data": { ... } }.
            var element = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? data
                : root;
            var status = element.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
            var lastEventAt = element.TryGetProperty("last_event_at", out var lastEventElement) && lastEventElement.ValueKind == JsonValueKind.String
                ? lastEventElement.GetString()
                : null;
            return (string.IsNullOrWhiteSpace(status) ? "unknown" : status!, lastEventAt);
        }
        catch (JsonException)
        {
            return ("unknown", null);
        }
    }

    private static string? ResolveBearerAccessToken(HttpContext http)
    {
        var accessToken = http.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (accessToken.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            accessToken = accessToken[bearerPrefix.Length..].Trim();

        return string.IsNullOrWhiteSpace(accessToken) ? null : accessToken;
    }

    private static string? ResolveSubjectId(ClaimsPrincipal principal)
    {
        foreach (var claimType in new[]
                 {
                     "uid",
                     "sub",
                     ClaimTypes.NameIdentifier,
                     "user_id",
                 })
        {
            var value = NormalizeOptional(principal.FindFirst(claimType)?.Value);
            if (value is not null)
                return value;
        }

        return null;
    }

    private static async Task<IResult> HandleDeleteRegistrationAsync(
        string registrationId,
        HttpContext http,
        [FromServices] ChannelRegistrationCommandFacade commandFacade,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] INyxChannelBotDeprovisioningService deprovision,
        CancellationToken ct)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: delete endpoint queried then dispatched unregister through raw helpers.
        //   New principle: query remains readmodel existence check; write enters typed command facade.
        // Deprovision (06-25-channel-delete-nyxid-deprovision):
        //   Delete clears Aevatar-owned NyxID resources (conversation route → Agent Key → Vault
        //   secret) before tombstoning the local mirror. NyxID channel bots are user-managed
        //   inventory and are not deleted by local registration removal. Agent Key deletion is a
        //   hard gate; route and Vault cleanup failures are warnings.
        var registration = await queryPort.GetAsync(registrationId, ct);
        if (registration is null)
            return Results.NotFound(new { error = "Registration not found" });

        var accessToken = ResolveBearerAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();

        var deprovisionResult = await deprovision.DeprovisionAsync(
            accessToken,
            NyxChannelBotDeprovisioningRequest.FromRegistration(registration),
            ct);

        if (!deprovisionResult.Succeeded)
        {
            return Results.Json(
                new
                {
                    error = "nyx_agent_key_delete_failed",
                    registration_id = registrationId,
                    note = "A required NyxID resource could not be deleted; the local registration was kept so you can retry.",
                },
                statusCode: StatusCodes.Status502BadGateway);
        }

        await commandFacade.UnregisterAsync(registrationId, ct);
        return Results.Ok(new { status = "deleted", warnings = deprovisionResult.Warnings });
    }

    /// <summary>
    /// Diagnostic: sends a test reply directly through the platform adapter,
    /// bypassing the full LLM chat flow. Isolates whether the reply path
    /// (NyxID proxy → platform API) is working.
    /// </summary>
    private static async Task<IResult> HandleTestReplyAsync(
        string registrationId,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        CancellationToken ct)
    {
        var registration = await queryPort.GetAsync(registrationId, ct);
        if (registration is null)
            return Results.NotFound(new { error = "Registration not found" });

        return Results.Json(new
        {
            error = "Direct platform reply diagnostics are retired. Validate replies through Nyx relay callback acceptance and channel-relay/reply instead.",
            registration_id = registrationId,
            platform = registration.Platform,
            nyx_provider_slug = registration.NyxProviderSlug,
        }, statusCode: StatusCodes.Status410Gone);
    }

    private static Task<IResult> HandleGetDiagnosticErrorsAsync()
    {
        return Task.FromResult<IResult>(Results.Json(new
        {
            error = "Channel runtime process-local diagnostic history is retired. Use logs, metrics, traces, or actor/projection-backed readmodel diagnostics.",
        }, statusCode: StatusCodes.Status410Gone));
    }

    private static ChannelRegistrationServiceSelection CurrentServiceSelection(
        ChannelBotRegistrationEntry registration) =>
        registration.AuthorizationMode == ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist
            ? ChannelRegistrationServiceSelection.Explicit(
                registration.RegistrationServiceAllowlist?.ServiceIds.ToArray() ?? [])
            : ChannelRegistrationServiceSelection.NyxIdDefaultSpecified();

    private static object MapRegistrationListRow(
        NyxChannelBotRecord bot,
        ChannelBotRegistrationSnapshot? snapshot,
        string? callerScope)
    {
        if (snapshot is null)
        {
            return new
            {
                id = (string?)null,
                platform = bot.Platform,
                label = bot.Label,
                registration_mode = "nyx_channel_bot_adoption",
                binding_status = "unbound",
                availability_status = MapNyxChannelBotAvailability(bot),
                nyx_status = bot.Status,
                authorization_mode = (string?)null,
                service_ids = (IReadOnlyList<string>?)null,
                state_version = (long?)null,
                nyx_provider_slug = ResolveDefaultProviderSlug(bot.Platform),
                callback_url = string.Empty,
                webhook_url = bot.WebhookUrl,
                nyx_channel_bot_id = bot.Id,
                nyx_agent_api_key_id = string.Empty,
                nyx_conversation_route_id = string.Empty,
                skill_name = string.Empty,
                has_instructions = false,
                has_tool_set_refs = false,
                has_extra_tool_names = false,
                nyxid_service_selectors = Array.Empty<NyxIdServiceSelectorResponse>(),
                agent_key = new { api_key_id = string.Empty, ready = false, status = "missing" },
                workflow_result_delivery_status = "unbound",
                workflow_result_delivery_failure_phase = (string?)null,
                workflow_result_delivery_failure_reason = (string?)null,
                owned = true,
            };
        }

        var e = snapshot.Registration;
        var capabilityStatus = ChannelWorkflowResultDeliveryCapability.Resolve(e);
        var repairFailed = capabilityStatus ==
            ChannelWorkflowResultDeliveryCapabilityStatus.RepairFailed;
        return new
        {
            id = e.Id,
            platform = string.IsNullOrWhiteSpace(e.Platform) ? bot.Platform : e.Platform,
            label = bot.Label,
            registration_mode = "nyx_relay_webhook",
            binding_status = "bound",
            availability_status = MapNyxChannelBotAvailability(bot),
            nyx_status = bot.Status,
            authorization_mode = MapAuthorizationMode(e),
            service_ids = MapRegistrationServiceIds(e),
            state_version = snapshot.StateVersion,
            nyx_provider_slug = e.NyxProviderSlug,
            callback_url = string.Empty,
            webhook_url = string.IsNullOrWhiteSpace(e.WebhookUrl) ? bot.WebhookUrl : e.WebhookUrl,
            nyx_channel_bot_id = e.NyxChannelBotId,
            nyx_agent_api_key_id = e.NyxAgentApiKeyId,
            nyx_conversation_route_id = e.NyxConversationRouteId,
            skill_name = ResolveSkillName(e.RuntimeConfig, e.DefaultSkillName),
            has_instructions = !string.IsNullOrWhiteSpace(e.RuntimeConfig?.Instructions),
            has_tool_set_refs = e.RuntimeConfig?.ToolSetRefs.Count > 0,
            has_extra_tool_names = e.RuntimeConfig?.ExtraToolNames.Count > 0,
            nyxid_service_selectors = MapNyxIdServiceSelectors(e.RuntimeConfig),
            agent_key = MapAgentKeyStatus(e),
            workflow_result_delivery_status = MapCapabilityStatus(e, capabilityStatus),
            workflow_result_delivery_failure_phase = repairFailed
                ? MapRepairPhase(e.WorkflowResultDeliveryRepair?.FailurePhase ??
                    ChannelWorkflowResultDeliveryRepairPhase.Unspecified)
                : null,
            workflow_result_delivery_failure_reason = repairFailed
                ? MapRepairFailureReason(e.WorkflowResultDeliveryRepair?.FailureReason ??
                    ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified)
                : null,
            owned = string.Equals(e.ScopeId, callerScope, StringComparison.Ordinal),
        };
    }

    private static string MapNyxChannelBotAvailability(NyxChannelBotRecord bot) =>
        bot.Active ? "available" : "unavailable";

    private static bool CallerOwnsRegistration(HttpContext http, ChannelBotRegistrationEntry registration)
    {
        var callerScopeId = ResolveScopeId(http, null, required: false).ScopeId;
        return !string.IsNullOrWhiteSpace(callerScopeId) &&
            string.Equals(registration.ScopeId, callerScopeId, StringComparison.Ordinal);
    }

    private static object MapRegistrationDetail(ChannelBotRegistrationSnapshot snapshot)
    {
        var entry = snapshot.Registration;
        var capabilityStatus = ChannelWorkflowResultDeliveryCapability.Resolve(entry);
        var repairFailed = capabilityStatus ==
            ChannelWorkflowResultDeliveryCapabilityStatus.RepairFailed;
        return new
        {
            id = entry.Id,
            platform = entry.Platform,
            label = entry.Id,
            registration_mode = "nyx_relay_webhook",
            binding_status = "bound",
            authorization_mode = MapAuthorizationMode(entry),
            service_ids = MapRegistrationServiceIds(entry),
            runtime_config = MapRuntimeConfig(entry.RuntimeConfig),
            skill_name = ResolveSkillName(entry.RuntimeConfig, entry.DefaultSkillName),
            state_version = snapshot.StateVersion,
            nyx_provider_slug = entry.NyxProviderSlug,
            webhook_url = entry.WebhookUrl,
            nyx_channel_bot_id = entry.NyxChannelBotId,
            nyx_agent_api_key_id = entry.NyxAgentApiKeyId,
            nyx_conversation_route_id = entry.NyxConversationRouteId,
            agent_key = MapAgentKeyStatus(entry),
            workflow_result_delivery_status = MapCapabilityStatus(entry, capabilityStatus),
            workflow_result_delivery_failure_phase = repairFailed
                ? MapRepairPhase(entry.WorkflowResultDeliveryRepair?.FailurePhase ??
                    ChannelWorkflowResultDeliveryRepairPhase.Unspecified)
                : null,
            workflow_result_delivery_failure_reason = repairFailed
                ? MapRepairFailureReason(entry.WorkflowResultDeliveryRepair?.FailureReason ??
                    ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified)
                : null,
            owned = true,
        };
    }

    private static object MapRuntimeConfig(ChannelBotRuntimeConfig? config) => new
    {
        instructions = config?.Instructions ?? string.Empty,
        tool_set_refs = config?.ToolSetRefs.ToArray() ?? Array.Empty<string>(),
        extra_tool_names = config?.ExtraToolNames.ToArray() ?? Array.Empty<string>(),
        nyxid_service_selectors = MapNyxIdServiceSelectors(config),
        credential_source_mode = MapCredentialSourceMode(config?.CredentialSourceMode ??
            ChannelBotRuntimeCredentialSourceMode.Unspecified),
    };

    private static string ResolveSkillName(ChannelBotRuntimeConfig? config, string? fallbackName) =>
        config?.DefaultSkill?.Name ?? fallbackName ?? string.Empty;

    private static string NormalizeDefaultSkillName(string? value) =>
        (value ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();

    private static string ResolveWebhookBaseUrl(HttpContext http, string? configuredWebhookBaseUrl)
    {
        var configured = configuredWebhookBaseUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var host = http.Request.Host.Value;
        return string.IsNullOrWhiteSpace(host)
            ? string.Empty
            : $"{http.Request.Scheme}://{host}";
    }

    private static IReadOnlyList<NyxIdServiceSelectorResponse> MapNyxIdServiceSelectors(
        ChannelBotRuntimeConfig? config) =>
        config?.NyxidServiceSelectors
            .Select(static selector => new NyxIdServiceSelectorResponse(
                selector.ServiceSlug ?? string.Empty,
                selector.EndpointNames.ToArray()))
            .ToArray() ?? [];

    private static object MapAgentKeyStatus(ChannelBotRegistrationEntry entry) => new
    {
        api_key_id = entry.ChannelAgentKey?.ApiKeyId ?? entry.NyxAgentApiKeyId ?? string.Empty,
        ready = entry.ChannelAgentKey?.SecretReference is not null ||
            entry.WorkflowResultDeliveryCredential is not null,
        status = entry.ChannelAgentKey?.SecretReference is not null ||
            entry.WorkflowResultDeliveryCredential is not null
                ? "ready"
                : "missing",
    };

    private static object MapCredentialSource(NyxIdUserServiceCredentialSource source) => new
    {
        kind = source.Kind switch
        {
            NyxIdUserServiceCredentialSourceKind.Personal => "personal",
            NyxIdUserServiceCredentialSourceKind.Organization => "organization",
            _ => "unspecified",
        },
        organization_id = source.OrganizationId ?? string.Empty,
        organization_role = source.OrganizationRole switch
        {
            NyxIdOrganizationRole.Admin => "admin",
            NyxIdOrganizationRole.Member => "member",
            NyxIdOrganizationRole.Viewer => "viewer",
            _ => "unspecified",
        },
        allowed = source.Allowed,
    };

    private static string MapCredentialSourceMode(ChannelBotRuntimeCredentialSourceMode mode) => mode switch
    {
        ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey => "registration_agent_key",
        ChannelBotRuntimeCredentialSourceMode.SenderBinding => "sender_binding",
        ChannelBotRuntimeCredentialSourceMode.Unspecified => "unspecified",
        _ => "unsupported",
    };

    private static int ResolveProvisioningFailureStatusCode(string? error)
    {
        var reason = error ?? string.Empty;
        return reason switch
        {
            "unsupported_platform" => StatusCodes.Status409Conflict,
            "missing_access_token" => StatusCodes.Status401Unauthorized,
            "missing_app_id" or "missing_app_secret" or "missing_verification_token" or "missing_bot_token" or "missing_webhook_base_url" or "missing_scope_id" or "insecure_webhook_base_url" => StatusCodes.Status400BadRequest,
            "channel_authorization_contract_invalid" => StatusCodes.Status409Conflict,
            "invalid_runtime_config" => StatusCodes.Status400BadRequest,
            "channel_bot_already_exists" or "channel_bot_already_bound" or "registration_id_already_exists" => StatusCodes.Status409Conflict,
            "ambiguous_channel_bot_route" or "channel_route_not_accessible" => StatusCodes.Status409Conflict,
            "channel_agent_key_write_gate_closed" => StatusCodes.Status503ServiceUnavailable,
            "secret_vault_unavailable" => StatusCodes.Status503ServiceUnavailable,
            "service_owner_forbidden" => StatusCodes.Status403Forbidden,
            "nyxid_user_service_not_accessible" => StatusCodes.Status404NotFound,
            "scope_plan_changed" => StatusCodes.Status409Conflict,
            "nyxid_scope_plan_unavailable" or "channel_service_connection_unavailable" => StatusCodes.Status502BadGateway,
            "nyx_base_url_not_configured" => StatusCodes.Status500InternalServerError,
            // A downstream NyxID channel-bot uniqueness conflict (NyxID allows one active bot per
            // app across all accounts) is a real Conflict, not a gateway failure. Surface 409 so the
            // caller learns the app is already registered — possibly under another account this
            // registration cannot auto-clean — instead of an opaque 502 that reads as an outage.
            _ when reason.Contains("nyx_status=409", StringComparison.Ordinal)
                || reason.Contains("already registered", StringComparison.OrdinalIgnoreCase)
                => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status502BadGateway,
        };
    }

    private static string? MapAuthorizationMode(ChannelBotRegistrationEntry entry) =>
        entry.AuthorizationMode switch
        {
            ChannelRegistrationAuthorizationMode.NyxidDefault => "nyxid_default",
            ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist =>
                "explicit_service_allowlist",
            ChannelRegistrationAuthorizationMode.Unspecified => null,
            _ => "unsupported",
        };

    private static IReadOnlyList<string>? MapRegistrationServiceIds(
        ChannelBotRegistrationEntry entry) =>
        entry.AuthorizationMode == ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist &&
        entry.RegistrationServiceAllowlist is not null
            ? entry.RegistrationServiceAllowlist.ServiceIds.ToArray()
            : null;

    private static int MapRepairResultStatusCode(
        ChannelWorkflowResultDeliveryRepairResultStatus status) => status switch
        {
            ChannelWorkflowResultDeliveryRepairResultStatus.Repaired or
                ChannelWorkflowResultDeliveryRepairResultStatus.AlreadyEnabled =>
                StatusCodes.Status200OK,
            ChannelWorkflowResultDeliveryRepairResultStatus.Repairing =>
                StatusCodes.Status202Accepted,
            ChannelWorkflowResultDeliveryRepairResultStatus.NotFound =>
                StatusCodes.Status404NotFound,
            ChannelWorkflowResultDeliveryRepairResultStatus.UnsupportedPlatform =>
                StatusCodes.Status409Conflict,
            ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed =>
                StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status502BadGateway,
        };

    private static string MapRepairResultStatus(
        ChannelWorkflowResultDeliveryRepairResultStatus status) => status switch
        {
            ChannelWorkflowResultDeliveryRepairResultStatus.Repaired => "repaired",
            ChannelWorkflowResultDeliveryRepairResultStatus.AlreadyEnabled => "already_enabled",
            ChannelWorkflowResultDeliveryRepairResultStatus.Repairing => "repairing",
            ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed => "repair_failed",
            ChannelWorkflowResultDeliveryRepairResultStatus.NotFound => "not_found",
            ChannelWorkflowResultDeliveryRepairResultStatus.UnsupportedPlatform =>
                "unsupported_platform",
            _ => "repair_failed",
        };

    private static string MapRepairCapabilityStatus(
        ChannelWorkflowResultDeliveryRepairResultStatus status) => status switch
        {
            ChannelWorkflowResultDeliveryRepairResultStatus.Repaired or
                ChannelWorkflowResultDeliveryRepairResultStatus.AlreadyEnabled => "enabled",
            ChannelWorkflowResultDeliveryRepairResultStatus.Repairing => "repairing",
            ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed => "repair_failed",
            ChannelWorkflowResultDeliveryRepairResultStatus.NotFound or
                ChannelWorkflowResultDeliveryRepairResultStatus.UnsupportedPlatform =>
                "repair_required",
            _ => "repair_failed",
        };

    private static string MapRepairResultNote(
        ChannelWorkflowResultDeliveryRepairResultStatus status) => status switch
        {
            ChannelWorkflowResultDeliveryRepairResultStatus.Repaired =>
                "Workflow result delivery was repaired. No Lark developer-console changes are required.",
            ChannelWorkflowResultDeliveryRepairResultStatus.AlreadyEnabled =>
                "Workflow result delivery is already enabled.",
            ChannelWorkflowResultDeliveryRepairResultStatus.Repairing =>
                "Workflow result delivery repair is still in progress.",
            ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed =>
                "Workflow result delivery repair did not complete. Retry from the committed repair state.",
            ChannelWorkflowResultDeliveryRepairResultStatus.NotFound =>
                "Registration not found.",
            ChannelWorkflowResultDeliveryRepairResultStatus.UnsupportedPlatform =>
                "Workflow result delivery repair is supported only for Lark registrations.",
            _ => "Workflow result delivery repair did not complete.",
        };

    private static string MapCapabilityStatus(
        ChannelBotRegistrationEntry entry,
        ChannelWorkflowResultDeliveryCapabilityStatus status)
    {
        if (ChannelRegistrationAuthorizationContract.Classify(entry) ==
            ChannelRegistrationAuthorizationContractKind.Invalid)
        {
            return "contract_invalid";
        }

        return status switch
        {
            ChannelWorkflowResultDeliveryCapabilityStatus.Enabled => "enabled",
            ChannelWorkflowResultDeliveryCapabilityStatus.RepairRequired => "repair_required",
            ChannelWorkflowResultDeliveryCapabilityStatus.Repairing => "repairing",
            ChannelWorkflowResultDeliveryCapabilityStatus.RepairFailed => "repair_failed",
            ChannelWorkflowResultDeliveryCapabilityStatus.Unspecified => "repair_required",
            _ => "repair_required",
        };
    }

    private static string MapRepairPhase(
        ChannelWorkflowResultDeliveryRepairPhase phase) => phase switch
        {
            ChannelWorkflowResultDeliveryRepairPhase.RequestAdmission => "request_admission",
            ChannelWorkflowResultDeliveryRepairPhase.RotatedKeyRecovery => "rotated_key_recovery",
            ChannelWorkflowResultDeliveryRepairPhase.ApiKeyRotation => "api_key_rotation",
            ChannelWorkflowResultDeliveryRepairPhase.VaultStorage => "vault_storage",
            ChannelWorkflowResultDeliveryRepairPhase.CredentialPreparation =>
                "credential_preparation",
            ChannelWorkflowResultDeliveryRepairPhase.RouteRebinding => "route_rebinding",
            ChannelWorkflowResultDeliveryRepairPhase.ActorCompletion => "actor_completion",
            ChannelWorkflowResultDeliveryRepairPhase.Unspecified => "unspecified",
            _ => "unspecified",
        };

    private static string MapRepairFailureReason(
        ChannelWorkflowResultDeliveryRepairFailureReason reason) => reason switch
        {
            ChannelWorkflowResultDeliveryRepairFailureReason.RegistrationNotFound =>
                "registration_not_found",
            ChannelWorkflowResultDeliveryRepairFailureReason.UnauthorizedOwner =>
                "unauthorized_owner",
            ChannelWorkflowResultDeliveryRepairFailureReason.UnsupportedPlatform =>
                "unsupported_platform",
            ChannelWorkflowResultDeliveryRepairFailureReason.AlreadyEnabled => "already_enabled",
            ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest => "invalid_request",
            ChannelWorkflowResultDeliveryRepairFailureReason.RequestConflict => "request_conflict",
            ChannelWorkflowResultDeliveryRepairFailureReason.StaleActiveKey => "stale_active_key",
            ChannelWorkflowResultDeliveryRepairFailureReason.RotationFailed => "rotation_failed",
            ChannelWorkflowResultDeliveryRepairFailureReason.VaultStorageFailed =>
                "vault_storage_failed",
            ChannelWorkflowResultDeliveryRepairFailureReason.RouteUpdateFailed =>
                "route_update_failed",
            ChannelWorkflowResultDeliveryRepairFailureReason.CompletionFailed =>
                "completion_failed",
            ChannelWorkflowResultDeliveryRepairFailureReason.AmbiguousRotatedKeyRecovery =>
                "ambiguous_rotated_key_recovery",
            ChannelWorkflowResultDeliveryRepairFailureReason.ObservationUnavailable =>
                "observation_unavailable",
            ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified => "unspecified",
            _ => "unspecified",
        };

    private static ScopeIdResolution ResolveScopeId(HttpContext http, string? explicitScopeId, bool required)
    {
        var explicitNormalized = NormalizeOptional(explicitScopeId);
        var claimNormalized = NormalizeOptional(http.User.FindFirst("scope_id")?.Value);
        if (explicitNormalized is not null &&
            claimNormalized is not null &&
            !string.Equals(explicitNormalized, claimNormalized, StringComparison.Ordinal))
        {
            return new ScopeIdResolution(null, "scope_id does not match the authenticated scope");
        }

        var resolved = explicitNormalized ?? claimNormalized;
        if (required && resolved is null)
            return new ScopeIdResolution(null, "scope_id is required");

        return new ScopeIdResolution(resolved, null);
    }

    private static ScopeIdResolution ResolveRegistrationOwnerScopeId(
        HttpContext http,
        string? explicitScopeId)
    {
        var claimNormalized = NormalizeOptional(http.User.FindFirst("scope_id")?.Value);
        if (claimNormalized is null)
            return new ScopeIdResolution(null, "scope_id is required");

        var explicitNormalized = NormalizeOptional(explicitScopeId);
        if (explicitNormalized is not null &&
            !string.Equals(explicitNormalized, claimNormalized, StringComparison.Ordinal))
        {
            return new ScopeIdResolution(null, "scope_id does not match the authenticated scope");
        }

        return new ScopeIdResolution(claimNormalized, null);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record ScopeIdResolution(string? ScopeId, string? Error);

    private sealed record NyxIdServiceSelectorResponse(
        string ServiceSlug,
        IReadOnlyList<string> EndpointNames);

    private sealed record RuntimeConfigFieldError(
        string Field,
        string Code,
        string Message);

    private static class ChannelBotRuntimeConfigValidation
    {
        private const int MaxInstructionsLength = 4000;
        private const int MaxShortValueLength = 128;
        private const int MaxToolSetRefs = 32;
        private const int MaxExtraToolNames = 64;
        private const int MaxSelectors = 16;
        private const int MaxEndpointNames = 64;

        public static IReadOnlyList<RuntimeConfigFieldError> ValidateStructure(
            ChannelBotRuntimeConfig? config)
        {
            var errors = new List<RuntimeConfigFieldError>();
            if (config is null)
                return errors;

            AddLengthError(errors, "runtime_config.instructions", config.Instructions, MaxInstructionsLength);
            if (config.DefaultSkill is not null)
            {
                AddLengthError(errors, "runtime_config.default_skill.name", config.DefaultSkill.Name, MaxShortValueLength);
                AddLengthError(errors, "runtime_config.default_skill.version", config.DefaultSkill.Version, MaxShortValueLength);
                if (string.IsNullOrWhiteSpace(config.DefaultSkill.Name) &&
                    !string.IsNullOrWhiteSpace(config.DefaultSkill.Version))
                {
                    errors.Add(new RuntimeConfigFieldError(
                        "runtime_config.default_skill.version",
                        "default_skill_name_required",
                        "default_skill.version requires default_skill.name."));
                }
            }

            ValidateStringList(errors, "runtime_config.tool_set_refs", config.ToolSetRefs, MaxToolSetRefs, MaxShortValueLength);
            ValidateStringList(errors, "runtime_config.extra_tool_names", config.ExtraToolNames, MaxExtraToolNames, MaxShortValueLength);
            ValidateSelectors(errors, config);
            return errors;
        }

        public static IReadOnlyList<RuntimeConfigFieldError> ValidateSelectorAuthorization(
            ChannelBotRuntimeConfig? config,
            ChannelRegistrationAuthorizationMode authorizationMode,
            IReadOnlyList<string> verifiedServiceSlugs)
        {
            var errors = new List<RuntimeConfigFieldError>();
            if (config is null || config.NyxidServiceSelectors.Count == 0)
                return errors;

            if (authorizationMode != ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)
            {
                errors.Add(new RuntimeConfigFieldError(
                    "runtime_config.nyxid_service_selectors",
                    "explicit_service_allowlist_required",
                    "nyxid_service_selectors require explicit_service_allowlist authorization."));
                return errors;
            }

            var authorizedSelectorSlugs = verifiedServiceSlugs
                .Where(static slug => !string.IsNullOrWhiteSpace(slug))
                .ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < config.NyxidServiceSelectors.Count; index++)
            {
                var serviceSlug = config.NyxidServiceSelectors[index].ServiceSlug;
                if (authorizedSelectorSlugs.Contains(serviceSlug))
                    continue;

                errors.Add(new RuntimeConfigFieldError(
                    $"runtime_config.nyxid_service_selectors[{index}].service_slug",
                    "service_not_authorized",
                    "runtime selector service_slug must come from the verified registration service authorization."));
            }

            return errors;
        }

        private static void ValidateSelectors(
            ICollection<RuntimeConfigFieldError> errors,
            ChannelBotRuntimeConfig config)
        {
            if (config.NyxidServiceSelectors.Count > MaxSelectors)
            {
                errors.Add(new RuntimeConfigFieldError(
                    "runtime_config.nyxid_service_selectors",
                    "too_many_items",
                    $"nyxid_service_selectors supports at most {MaxSelectors} items."));
            }

            var seenSlugs = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < config.NyxidServiceSelectors.Count; index++)
            {
                var selector = config.NyxidServiceSelectors[index];
                var field = $"runtime_config.nyxid_service_selectors[{index}].service_slug";
                AddLengthError(errors, field, selector.ServiceSlug, MaxShortValueLength);
                if (!seenSlugs.Add(selector.ServiceSlug))
                {
                    errors.Add(new RuntimeConfigFieldError(
                        field,
                        "duplicate_service_slug",
                        "Each nyxid service selector must target a distinct service_slug."));
                }

                ValidateStringList(
                    errors,
                    $"runtime_config.nyxid_service_selectors[{index}].endpoint_names",
                    selector.EndpointNames,
                    MaxEndpointNames,
                    MaxShortValueLength);
            }
        }

        private static void ValidateStringList(
            ICollection<RuntimeConfigFieldError> errors,
            string field,
            IReadOnlyList<string> values,
            int maxCount,
            int maxLength)
        {
            if (values.Count > maxCount)
            {
                errors.Add(new RuntimeConfigFieldError(
                    field,
                    "too_many_items",
                    $"{field} supports at most {maxCount} items."));
            }

            var seenValues = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index];
                AddLengthError(errors, $"{field}[{index}]", value, maxLength);
                if (!seenValues.Add(value))
                {
                    errors.Add(new RuntimeConfigFieldError(
                        $"{field}[{index}]",
                        "duplicate_value",
                        "Repeated values are not allowed."));
                }
            }
        }

        private static void AddLengthError(
            ICollection<RuntimeConfigFieldError> errors,
            string field,
            string value,
            int maxLength)
        {
            if ((value?.Length ?? 0) <= maxLength)
                return;

            errors.Add(new RuntimeConfigFieldError(
                field,
                "too_long",
                $"{field} must be {maxLength} characters or fewer."));
        }
    }

    private static IReadOnlyList<NyxChannelBotRecord> ParseNyxChannelBots(string response)
    {
        using var document = JsonDocument.Parse(response);
        return TryGetArray(document.RootElement, ["data", "channel_bots", "channelBots", "items", "bots"], out var array)
            ? array.EnumerateArray().Select(ParseNyxChannelBotElement).Where(static bot => bot is not null).Select(static bot => bot!).ToArray()
            : [];
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

    private sealed record RegistrationRequest(
        string? RegistrationId,
        string? NyxChannelBotId,
        string? NyxConversationRouteId,
        string? NyxProviderSlug,
        [property: JsonIgnore] ChannelBotRuntimeConfig? RuntimeConfig = null);

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
        VerifiedChannelRegistrationAuthorizationPlan? Plan);

    /// <summary>
    /// Builds the default Nyx provider slug echoed back to the client when the registration request
    /// did not pin <c>nyx_provider_slug</c>. The convention is <c>api-{platform}-bot</c>, so adding
    /// a new platform doesn't need a new switch arm and a future <c>discord</c> registration would
    /// surface <c>api-discord-bot</c> rather than silently echoing <c>api-lark-bot</c>.
    /// </summary>
    private static string ResolveDefaultProviderSlug(string platform) =>
        $"api-{platform}-bot";
}
