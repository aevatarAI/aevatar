using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
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
        group.MapPost("/registrations", HandleRegisterAsync)
            .WithEndpointAudit(
                "channel.registration.create",
                AuditSensitivityLevel.Confidential,
                "channel-registration",
                EndpointAuditTargetResolvers.Static("channel-registration", "new"),
                ChannelRegistrationRequestSummary)
            .RequireAuthorization();
        group.MapGet("/registrations", HandleListRegistrationsAsync).RequireAuthorization();
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
        [FromServices] ChannelRelayRegistrationFacade registrationFacade,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: endpoint selected platform service and invoked provisioning directly.
        //   New principle: Host adapts HTTP only; typed application facade owns registration command flow.
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Registration");

        RegistrationRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<RegistrationRequest>(RegistrationJsonOptions, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid registration request payload");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }

        if (request is null ||
            string.IsNullOrWhiteSpace(request.Platform))
        {
            return Results.BadRequest(new { error = "platform is required" });
        }

        var accessToken = ResolveBearerAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
        {
            return Results.BadRequest(new { error = "webhook_base_url is required for Nyx-backed relay provisioning" });
        }

        var scopeResolution = ResolveScopeId(http, request.ScopeId, required: true);
        if (scopeResolution.Error is not null)
            return Results.BadRequest(new { error = scopeResolution.Error });

        var platformNormalized = request.Platform.Trim().ToLowerInvariant();
        var result = await registrationFacade.RegisterAsync(
            new ChannelRelayRegistrationRequest(
                Platform: platformNormalized,
                AccessToken: accessToken,
                WebhookBaseUrl: request.WebhookBaseUrl.Trim(),
                ScopeId: scopeResolution.ScopeId!,
                Label: request.Label?.Trim() ?? string.Empty,
                NyxProviderSlug: request.NyxProviderSlug?.Trim() ?? string.Empty,
                Lark: new NyxChannelLarkCredentials(
                    AppId: request.AppId?.Trim() ?? string.Empty,
                    AppSecret: request.AppSecret?.Trim() ?? string.Empty,
                    VerificationToken: request.VerificationToken?.Trim() ?? string.Empty,
                    EncryptKey: request.EncryptKey?.Trim() ?? string.Empty),
                Credentials: BuildCredentialsMap(platformNormalized, request),
                DefaultSkillName: request.DefaultSkillName?.Trim() ?? string.Empty),
            ct);

        var payload = new
        {
            status = result.Status,
            registration_id = result.RegistrationId ?? string.Empty,
            platform = result.Platform,
            nyx_provider_slug = string.IsNullOrWhiteSpace(request.NyxProviderSlug)
                ? ResolveDefaultProviderSlug(platformNormalized)
                : request.NyxProviderSlug.Trim(),
            nyx_channel_bot_id = result.NyxChannelBotId ?? string.Empty,
            nyx_agent_api_key_id = result.NyxAgentApiKeyId ?? string.Empty,
            nyx_conversation_route_id = result.NyxConversationRouteId ?? string.Empty,
            relay_callback_url = result.RelayCallbackUrl ?? string.Empty,
            webhook_url = result.WebhookUrl ?? string.Empty,
            workflow_result_delivery_status = result.WorkflowResultDeliveryEnabled
                ? "enabled"
                : "repair_required",
            error = result.Error ?? string.Empty,
            note = result.Note ?? string.Empty,
        };

        if (result.Succeeded)
            return Results.Accepted(value: payload);

        var statusCode = ResolveProvisioningFailureStatusCode(result.Error);
        logger.LogWarning(
            "Nyx-backed channel provisioning rejected: platform={Platform}, statusCode={StatusCode}, error={Error}",
                result.Platform,
            statusCode,
            result.Error);
        return Results.Json(payload, statusCode: statusCode);
    }

    /// <summary>
    /// Lists channel-bot registrations. Scoped to the caller's own account by default
    /// (a tenant must not see other tenants' bots, and their status is only queryable
    /// for the caller's own bots anyway). <c>?scope=all</c> returns every account's bots
    /// but is gated on aevatar admin access, resolved server-side from the caller identity.
    /// </summary>
    private static async Task<IResult> HandleListRegistrationsAsync(
        HttpContext http,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] IPlatformAdminAuthorizer adminAuthorizer,
        string? scope,
        CancellationToken ct)
    {
        var callerScope = ResolveScopeId(http, null, required: false).ScopeId;
        var wantsAll = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);

        if (wantsAll)
        {
            var token = ResolveBearerAccessToken(http);
            var caller = string.IsNullOrWhiteSpace(token)
                ? PlatformCaller.NotElevated
                : await adminAuthorizer.ResolveCallerAsync(token, ct);
            if (!caller.IsElevated)
            {
                return Results.Json(
                    new { error = "scope_admin_required", message = "Listing channel bots across accounts requires aevatar admin access." },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        var registrations = await queryPort.QueryAllAsync(ct);
        var visible = wantsAll
            ? registrations
            : registrations.Where(e => string.Equals(e.ScopeId, callerScope, StringComparison.Ordinal));

        var result = visible.Select(e =>
        {
            var capabilityStatus = ChannelWorkflowResultDeliveryCapability.Resolve(e);
            var repairFailed = capabilityStatus ==
                ChannelWorkflowResultDeliveryCapabilityStatus.RepairFailed;
            return new
            {
                id = e.Id,
                platform = e.Platform,
                registration_mode = "nyx_relay_webhook",
                nyx_provider_slug = e.NyxProviderSlug,
                scope_id = e.ScopeId,
                callback_url = string.Empty,
                webhook_url = e.WebhookUrl,
                nyx_channel_bot_id = e.NyxChannelBotId,
                nyx_agent_api_key_id = e.NyxAgentApiKeyId,
                nyx_conversation_route_id = e.NyxConversationRouteId,
                default_skill_name = e.DefaultSkillName,
                workflow_result_delivery_status = MapCapabilityStatus(capabilityStatus),
                workflow_result_delivery_failure_phase = repairFailed
                    ? MapRepairPhase(e.WorkflowResultDeliveryRepair?.FailurePhase ??
                        ChannelWorkflowResultDeliveryRepairPhase.Unspecified)
                    : null,
                workflow_result_delivery_failure_reason = repairFailed
                    ? MapRepairFailureReason(e.WorkflowResultDeliveryRepair?.FailureReason ??
                        ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified)
                    : null,
                // Whether this bot belongs to the caller's account. Cross-account bots
                // (admin all-view) cannot have their live status read from NyxID.
                owned = string.Equals(e.ScopeId, callerScope, StringComparison.Ordinal),
            };
        });

        return Results.Json(result, RegistrationJsonOptions);
    }

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
        var capabilityStatusValue = MapCapabilityStatus(capabilityStatus);
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
        //   Delete is the reverse of register — tear down the NyxID side (conversation route →
        //   channel-bot → relay api-key) BEFORE tombstoning the local mirror, so deleting a bot
        //   leaves no orphaned NyxID resources and the same app re-registers cleanly. A NyxID 404
        //   is success (idempotent). A hard channel-bot delete failure returns a non-2xx and does
        //   NOT tombstone the local mirror (row stays visible/retryable). Residual route/api-key
        //   cleanup failures are surfaced as warnings but never block the tombstone. NyxID
            //   channel-bot delete is owner-scoped, so an admin deleting another owner's
        //   foreign registration cannot delete that owner's NyxID bot — that hard-fails here and
        //   keeps the local mirror; a pure-local admin purge would be a separate explicit path.
        var registration = await queryPort.GetAsync(registrationId, ct);
        if (registration is null)
            return Results.NotFound(new { error = "Registration not found" });

        var accessToken = ResolveBearerAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();

        var deprovisionResult = await deprovision.DeprovisionAsync(
            accessToken,
            registration.NyxConversationRouteId,
            registration.NyxChannelBotId,
            registration.NyxAgentApiKeyId,
            ct);

        if (!deprovisionResult.Succeeded)
        {
            // Hard channel-bot delete failure: leave the local mirror intact so the caller can
            // retry and the registration row stays visible (no silent half-dead orphan).
            return Results.Json(
                new
                {
                    error = "nyx_channel_bot_delete_failed",
                    registration_id = registrationId,
                    note = "The NyxID channel-bot could not be deleted; the local registration was kept so you can retry.",
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

    private static int ResolveProvisioningFailureStatusCode(string? error)
    {
        var reason = error ?? string.Empty;
        return reason switch
        {
            "unsupported_platform" => StatusCodes.Status409Conflict,
            "missing_access_token" => StatusCodes.Status401Unauthorized,
            "missing_app_id" or "missing_app_secret" or "missing_verification_token" or "missing_bot_token" or "missing_webhook_base_url" or "missing_scope_id" or "insecure_webhook_base_url" => StatusCodes.Status400BadRequest,
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
        ChannelWorkflowResultDeliveryCapabilityStatus status) => status switch
        {
            ChannelWorkflowResultDeliveryCapabilityStatus.Enabled => "enabled",
            ChannelWorkflowResultDeliveryCapabilityStatus.RepairRequired => "repair_required",
            ChannelWorkflowResultDeliveryCapabilityStatus.Repairing => "repairing",
            ChannelWorkflowResultDeliveryCapabilityStatus.RepairFailed => "repair_failed",
            ChannelWorkflowResultDeliveryCapabilityStatus.Unspecified => "repair_required",
            _ => "repair_required",
        };

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

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record ScopeIdResolution(string? ScopeId, string? Error);

    private sealed record RegistrationRequest(
        string? Platform,
        string? NyxProviderSlug,
        string? ScopeId,
        string? WebhookBaseUrl,
        // Lark-specific (legacy explicit fields kept for backward compatibility; Telegram and
        // future platforms use the Credentials map below).
        string? AppId,
        string? AppSecret,
        string? VerificationToken,
        string? EncryptKey,
        // Telegram-specific shorthand: equivalent to Credentials["bot_token"].
        string? BotToken,
        // Platform-extensible credential bag. Per-platform provisioning services document
        // which keys they expect (e.g. Telegram reads "bot_token").
        IReadOnlyDictionary<string, string>? Credentials,
        string? Label,
        // Optional Ornn skill this bot's plain inbound messages are routed to
        // (deterministic channel→skill binding; message text becomes the skill args).
        string? DefaultSkillName);

    private static IReadOnlyDictionary<string, string>? BuildCredentialsMap(
        string platform,
        RegistrationRequest request)
    {
        var bag = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Credentials is { Count: > 0 } incoming)
        {
            foreach (var (key, value) in incoming)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    bag[key] = value.Trim();
            }
        }

        if (string.Equals(platform, "telegram", StringComparison.OrdinalIgnoreCase) &&
            !bag.ContainsKey("bot_token") &&
            !string.IsNullOrWhiteSpace(request.BotToken))
        {
            bag["bot_token"] = request.BotToken!.Trim();
        }

        return bag.Count == 0 ? null : bag;
    }

    /// <summary>
    /// Builds the default Nyx provider slug echoed back to the client when the registration request
    /// did not pin <c>nyx_provider_slug</c>. The convention is <c>api-{platform}-bot</c>, so adding
    /// a new platform doesn't need a new switch arm and a future <c>discord</c> registration would
    /// surface <c>api-discord-bot</c> rather than silently echoing <c>api-lark-bot</c>.
    /// </summary>
    private static string ResolveDefaultProviderSlug(string platform) =>
        $"api-{platform}-bot";
}
