using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public static class ChannelCallbackEndpoints
{
    public static IEndpointRouteBuilder MapChannelCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channels").WithTags("ChannelRuntime");

        // Platform callback — receives webhooks directly from platforms. These are invoked by
        // external services (Lark, Telegram, …) without our JWT, so they must remain anonymous
        // even after the host applies an authenticated-by-default fallback policy.
        group.MapPost("/{platform}/callback/{registrationId}", HandleCallbackAsync).AllowAnonymous();

        // Registration CRUD — requires authentication
        group.MapPost("/registrations", HandleRegisterAsync).RequireAuthorization();
        group.MapGet("/registrations", HandleListRegistrationsAsync).RequireAuthorization();
        group.MapPost("/registrations/rebuild", HandleRebuildRegistrationsAsync).RequireAuthorization();
        group.MapPost("/registrations/repair-lark-mirror", HandleRepairLarkMirrorAsync).RequireAuthorization();
        group.MapDelete("/registrations/{registrationId}", HandleDeleteRegistrationAsync).RequireAuthorization();

        // Diagnostic: test reply path without going through full LLM chat
        group.MapPost("/registrations/{registrationId}/test-reply", HandleTestReplyAsync).RequireAuthorization();
        group.MapGet("/diagnostics/errors", HandleGetDiagnosticErrorsAsync).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Receives a platform webhook callback directly.
    /// 1. Handles verification challenges (returns immediately).
    /// 2. Parses inbound message.
    /// 3. Returns 200 OK immediately (platforms have short timeouts).
    /// 4. Fires background task: dispatch to actor, collect response, send reply via Nyx provider.
    /// </summary>
    private static Task<IResult> HandleCallbackAsync(
        HttpContext http,
        string platform,
        string registrationId)
    {
        var diagnostics = http.RequestServices.GetService<IChannelRuntimeDiagnostics>();
        RecordDiagnostic(diagnostics, "Callback:retired", platform, registrationId, "direct_callback_retired");
        return Task.FromResult<IResult>(Results.Json(
            new
            {
                error = "Direct platform callbacks are retired. ChannelRuntime now accepts only Nyx relay ingress for supported platforms.",
                registration_id = registrationId,
                platform,
                supported_ingress = "/api/webhooks/nyxid-relay",
            },
            statusCode: StatusCodes.Status410Gone));
    }

    // ─── Registration CRUD ───

    private static readonly JsonSerializerOptions RegistrationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext http,
        [FromServices] IEnumerable<INyxChannelBotProvisioningService> provisioningServices,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
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

        var platformNormalized = request.Platform.Trim().ToLowerInvariant();
        var provisioningServiceMap = BuildProvisioningServiceMap(provisioningServices);
        if (!provisioningServiceMap.TryGetValue(platformNormalized, out var provisioningService))
        {
            return Results.Conflict(new
            {
                error = $"Platform '{platformNormalized}' is not in the supported production contract. ChannelRuntime currently provisions relay registrations for: {string.Join(", ", provisioningServiceMap.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))}.",
            });
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

        var result = await provisioningService.ProvisionAsync(
            new NyxChannelBotProvisioningRequest(
                Platform: platformNormalized,
                AccessToken: accessToken,
                WebhookBaseUrl: request.WebhookBaseUrl.Trim(),
                ScopeId: scopeResolution.ScopeId!,
                Label: request.Label?.Trim() ?? string.Empty,
                NyxProviderSlug: request.NyxProviderSlug?.Trim() ?? string.Empty,
                Lark: new NyxChannelLarkCredentials(
                    AppId: request.AppId?.Trim() ?? string.Empty,
                    AppSecret: request.AppSecret?.Trim() ?? string.Empty,
                    VerificationToken: request.VerificationToken?.Trim() ?? string.Empty),
                Credentials: BuildCredentialsMap(platformNormalized, request)),
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

    private static async Task<IResult> HandleListRegistrationsAsync(
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        CancellationToken ct)
    {
        var registrations = await queryPort.QueryAllAsync(ct);
        var result = registrations.Select(e => new
        {
            id = e.Id,
            platform = e.Platform,
            registration_mode = "nyx_relay_webhook",
            nyx_provider_slug = e.NyxProviderSlug,
            scope_id = e.ScopeId,
            callback_url = string.Empty,
            nyx_channel_bot_id = e.NyxChannelBotId,
            nyx_agent_api_key_id = e.NyxAgentApiKeyId,
            nyx_conversation_route_id = e.NyxConversationRouteId,
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> HandleRebuildRegistrationsAsync(
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort dispatchPort,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] INyxRelayApiKeyOwnershipVerifier? apiKeyOwnershipVerifier,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Registration");
        ChannelRegistrationRebuildRequest? request;
        try
        {
            request = await ReadOptionalRebuildRequestAsync(http, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid channel registration rebuild request payload");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Unsupported channel registration rebuild request content type");
            return Results.BadRequest(new { error = "Unsupported content type. Use application/json for rebuild request payloads." });
        }

        var scopeResolution = ResolveScopeId(http, request?.ScopeId, required: false);
        if (scopeResolution.Error is not null)
            return Results.BadRequest(new { error = scopeResolution.Error });

        var accessToken = ResolveBearerAccessToken(http);
        int? observedRegistrationsBeforeRebuild = null;
        ChannelBotRegistrationScopeBackfillResult? backfill = null;
        var note = "Projection rebuild dispatched from authoritative channel-bot-registration-store state. Query-side registrations may take a moment to refresh.";

        try
        {
            var registrations = await queryPort.QueryAllAsync(ct);
            observedRegistrationsBeforeRebuild = registrations.Count;
            backfill = await ChannelBotRegistrationScopeBackfill.BackfillAsync(
                registrations,
                scopeResolution.ScopeId,
                new ChannelBotRegistrationScopeBackfillSelection(
                    request?.RegistrationId,
                    request?.NyxAgentApiKeyId,
                    request?.Force ?? false),
                actorRuntime,
                dispatchPort,
                new ChannelBotRegistrationScopeBackfillAuthorization(
                    accessToken,
                    apiKeyOwnershipVerifier),
                ct);
            if (backfill.EmptyScopeRegistrationsObserved > 0)
                note = $"{note} {backfill.Note}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Channel registration query failed before dispatching a manual rebuild");
            // Surface a known `unavailable` enum value (issue #391 review): callers
            // must always be able to branch on backfill_status, especially when
            // the read side is degraded.
            backfill = ChannelBotRegistrationScopeBackfill.Unavailable(ex.Message);
            note = $"Projection rebuild dispatched from authoritative channel-bot-registration-store state. {backfill.Note}";
        }

        await ChannelBotRegistrationStoreCommands.DispatchRebuildProjectionAsync(
            actorRuntime,
            dispatchPort,
            string.IsNullOrWhiteSpace(request?.Reason)
                ? "http_api_manual_rebuild"
                : request.Reason.Trim(),
            ct);

        return Results.Accepted(value: new
        {
            status = "accepted",
            actor_id = ChannelBotRegistrationGAgent.WellKnownId,
            observed_registrations_before_rebuild = observedRegistrationsBeforeRebuild,
            empty_scope_registrations_observed = backfill?.EmptyScopeRegistrationsObserved,
            empty_scope_registrations_backfilled = backfill?.RepairCommandsDispatched,
            // Machine-readable backfill outcome so CLI/UI callers do not misread
            // a 202 rebuild dispatch as a successful backfill (issue #391). The
            // catch path above guarantees a non-null value even when the read
            // side throws.
            backfill_status = backfill?.Status.ToWireString(),
            warnings = backfill?.Warnings ?? Array.Empty<string>(),
            note,
        });
    }

    /// <summary>
    /// Repairs the local <c>channel-bot-registration-store</c> mirror for a Lark
    /// bot whose Nyx-side resources (api-key, channel-bot, conversation-route)
    /// already exist but whose local <see cref="ChannelBotRegistrationDocument"/>
    /// is missing — typically after a namespace migration that destroyed the
    /// authoritative actor and left no entry to project. Idempotent: re-running
    /// against an already-mirrored registration returns <c>already_registered</c>
    /// without dispatching another <c>ChannelBotRegisterCommand</c>.
    ///
    /// Direct HTTP equivalent of the LLM-tool path
    /// <c>channel_registrations action=repair_lark_mirror</c>; see
    /// <c>docs/operations/2026-04-29-lark-mirror-recovery-runbook.md</c>. The
    /// preflight (<c>already_registered</c> short-circuit, scope-mismatch
    /// reject, empty-scope id reuse) MUST mirror the LLM-tool path —
    /// otherwise repeated calls without a <c>registration_id</c> mint a fresh
    /// id every time, and the resolver will later see multiple distinct
    /// scope ids for one Nyx api-key and refuse to route relay traffic.
    /// </summary>
    private static async Task<IResult> HandleRepairLarkMirrorAsync(
        HttpContext http,
        [FromServices] INyxLarkProvisioningService provisioningService,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Repair");

        RepairLarkMirrorRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<RepairLarkMirrorRequest>(RegistrationJsonOptions, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid repair-lark-mirror request payload");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }

        if (request is null)
            return Results.BadRequest(new { error = "request body is required" });

        if (string.IsNullOrWhiteSpace(request.NyxChannelBotId))
            return Results.BadRequest(new { error = "nyx_channel_bot_id is required" });
        if (string.IsNullOrWhiteSpace(request.NyxAgentApiKeyId))
            return Results.BadRequest(new { error = "nyx_agent_api_key_id is required" });
        if (string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
            return Results.BadRequest(new { error = "webhook_base_url is required" });

        var accessToken = ResolveBearerAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();

        var scopeResolution = ResolveScopeId(http, request.ScopeId, required: true);
        if (scopeResolution.Error is not null)
            return Results.BadRequest(new { error = scopeResolution.Error });

        var nyxChannelBotId = request.NyxChannelBotId.Trim();
        var nyxAgentApiKeyId = request.NyxAgentApiKeyId.Trim();
        var nyxConversationRouteId = request.NyxConversationRouteId?.Trim() ?? string.Empty;
        var requestedRegistrationId = request.RegistrationId?.Trim() ?? string.Empty;

        // Preflight against the local mirror so repeated calls converge on the
        // same registration id instead of minting a fresh one each time. Any
        // existing same-scope mirror short-circuits; cross-scope matches are
        // rejected to prevent api-key hijack via repair; empty-scope mirrors
        // (legacy entries from before scope was tracked) get reused so the
        // backfill path attaches a scope rather than diverging.
        ChannelBotRegistrationEntry? existing = null;
        try
        {
            var registrations = await queryPort.QueryAllAsync(ct);
            existing = registrations.FirstOrDefault(entry =>
                string.Equals(entry.Platform, "lark", StringComparison.OrdinalIgnoreCase) &&
                MatchesNyxIdentity(entry, nyxChannelBotId, nyxAgentApiKeyId, nyxConversationRouteId));
            if (existing is not null)
            {
                var existingScopeId = NormalizeOptional(existing.ScopeId);
                if (existingScopeId is not null)
                {
                    if (!string.Equals(existingScopeId, scopeResolution.ScopeId, StringComparison.Ordinal))
                    {
                        logger.LogWarning(
                            "Lark mirror repair rejected: matching mirror belongs to a different scope. registrationId={RegistrationId} existingScopeId={ExistingScopeId} requestedScopeId={RequestedScopeId}",
                            existing.Id,
                            existingScopeId,
                            scopeResolution.ScopeId);
                        return Results.BadRequest(new
                        {
                            error = "matching local Aevatar mirror belongs to a different scope_id",
                            registration_id = existing.Id,
                        });
                    }

                    return Results.Ok(new
                    {
                        status = "already_registered",
                        registration_id = existing.Id,
                        nyx_channel_bot_id = existing.NyxChannelBotId,
                        nyx_agent_api_key_id = existing.NyxAgentApiKeyId,
                        nyx_conversation_route_id = existing.NyxConversationRouteId,
                        webhook_url = existing.WebhookUrl,
                        nyx_provider_slug = string.IsNullOrWhiteSpace(existing.NyxProviderSlug)
                            ? "api-lark-bot"
                            : existing.NyxProviderSlug,
                        note = "Matching local Aevatar mirror already exists.",
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // Repair must remain usable when the read side is degraded —
            // logging only, falling through to the dispatch path.
            logger.LogWarning(
                ex,
                "Lark mirror repair preflight failed; falling through to dispatch without short-circuit. nyxChannelBotId={NyxChannelBotId}",
                nyxChannelBotId);
        }

        // Reuse the existing registration id when an empty-scope mirror exists
        // and the caller did not supply one, so the backfill path attaches a
        // scope instead of producing a parallel registration.
        if (string.IsNullOrWhiteSpace(requestedRegistrationId) && existing is not null)
            requestedRegistrationId = existing.Id;

        var result = await provisioningService.RepairLocalMirrorAsync(
            new NyxLarkMirrorRepairRequest(
                AccessToken: accessToken,
                RequestedRegistrationId: requestedRegistrationId,
                ScopeId: scopeResolution.ScopeId!,
                NyxProviderSlug: request.NyxProviderSlug?.Trim() ?? string.Empty,
                WebhookBaseUrl: request.WebhookBaseUrl.Trim(),
                NyxChannelBotId: nyxChannelBotId,
                NyxAgentApiKeyId: nyxAgentApiKeyId,
                NyxConversationRouteId: nyxConversationRouteId),
            ct);

        var payload = new
        {
            status = result.Status,
            registration_id = result.RegistrationId ?? string.Empty,
            nyx_channel_bot_id = result.NyxChannelBotId ?? string.Empty,
            nyx_agent_api_key_id = result.NyxAgentApiKeyId ?? string.Empty,
            nyx_conversation_route_id = result.NyxConversationRouteId ?? string.Empty,
            webhook_url = result.WebhookUrl ?? string.Empty,
            error = result.Error ?? string.Empty,
            note = result.Note ?? string.Empty,
        };

        if (result.Succeeded)
            return Results.Accepted(value: payload);

        var statusCode = ResolveProvisioningFailureStatusCode(result.Error);
        logger.LogWarning(
            "Lark mirror repair rejected: statusCode={StatusCode}, error={Error}",
            statusCode,
            result.Error);
        return Results.Json(payload, statusCode: statusCode);
    }

    private static bool MatchesNyxIdentity(
        ChannelBotRegistrationEntry entry,
        string nyxChannelBotId,
        string nyxAgentApiKeyId,
        string nyxConversationRouteId)
    {
        var hasConstraint = false;

        if (!MatchesIfProvided(entry.NyxChannelBotId, nyxChannelBotId, ref hasConstraint))
            return false;
        if (!MatchesIfProvided(entry.NyxAgentApiKeyId, nyxAgentApiKeyId, ref hasConstraint))
            return false;
        if (!MatchesIfProvided(entry.NyxConversationRouteId, nyxConversationRouteId, ref hasConstraint))
            return false;

        return hasConstraint;
    }

    private static bool MatchesIfProvided(string actual, string expected, ref bool hasConstraint)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return true;

        hasConstraint = true;
        return !string.IsNullOrWhiteSpace(actual) &&
               string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static string? ResolveBearerAccessToken(HttpContext http)
    {
        var accessToken = http.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (accessToken.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            accessToken = accessToken[bearerPrefix.Length..].Trim();

        return string.IsNullOrWhiteSpace(accessToken) ? null : accessToken;
    }

    private static async Task<IResult> HandleDeleteRegistrationAsync(
        string registrationId,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort dispatchPort,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        CancellationToken ct)
    {
        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return Results.NotFound(new { error = "Registration not found" });

        await ChannelBotRegistrationStoreCommands.DispatchUnregisterAsync(
            actorRuntime,
            dispatchPort,
            registrationId,
            ct);
        return Results.Ok(new { status = "deleted" });
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

    private static Task<IResult> HandleGetDiagnosticErrorsAsync(
        [FromServices] IChannelRuntimeDiagnostics? diagnostics)
    {
        var entries = diagnostics?.GetRecent()
                      ?? Array.Empty<ChannelRuntimeDiagnosticEntry>();

        return Task.FromResult<IResult>(Results.Ok(new
        {
            status = new
            {
                service_resolved = diagnostics != null,
                server_time = DateTimeOffset.UtcNow.ToString("O"),
                entry_count = entries.Count,
            },
            entries = entries.Select(entry => new
            {
                timestamp = entry.Timestamp.ToString("O"),
                stage = entry.Stage,
                platform = entry.Platform,
                registrationId = entry.RegistrationId,
                detail = entry.Detail,
            }),
        }));
    }

    private static void RecordDiagnostic(
        IChannelRuntimeDiagnostics? diagnostics,
        string stage,
        string platform,
        string registrationId,
        string? detail = null)
    {
        diagnostics?.Record(stage, platform, registrationId, detail);
    }

    private static int ResolveProvisioningFailureStatusCode(string? error)
    {
        return error switch
        {
            "unsupported_platform" => StatusCodes.Status409Conflict,
            "missing_access_token" => StatusCodes.Status401Unauthorized,
            "missing_app_id" or "missing_app_secret" or "missing_verification_token" or "missing_bot_token" or "missing_webhook_base_url" or "missing_scope_id" => StatusCodes.Status400BadRequest,
            "nyx_base_url_not_configured" => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status502BadGateway,
        };
    }

    private static IReadOnlyDictionary<string, INyxChannelBotProvisioningService> BuildProvisioningServiceMap(
        IEnumerable<INyxChannelBotProvisioningService> provisioningServices)
    {
        ArgumentNullException.ThrowIfNull(provisioningServices);

        var serviceMap = new Dictionary<string, INyxChannelBotProvisioningService>(StringComparer.OrdinalIgnoreCase);
        foreach (var provisioningService in provisioningServices)
        {
            if (provisioningService is null)
                continue;

            var platformKey = provisioningService.Platform?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(platformKey))
                continue;

            if (!serviceMap.TryAdd(platformKey, provisioningService))
            {
                throw new InvalidOperationException(
                    $"Multiple Nyx channel provisioning services are registered for platform '{platformKey}'.");
            }
        }

        return serviceMap;
    }

    private static async Task<ChannelRegistrationRebuildRequest?> ReadOptionalRebuildRequestAsync(
        HttpContext http,
        CancellationToken ct)
    {
        if (http.Request.ContentLength == 0)
            return null;
        if (http.Request.Body.CanSeek && http.Request.Body.Length == http.Request.Body.Position)
            return null;

        // ReadFromJsonAsync throws InvalidOperationException for unsupported content types.
        return await http.Request.ReadFromJsonAsync<ChannelRegistrationRebuildRequest>(RegistrationJsonOptions, ct);
    }

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

    private sealed record ChannelRegistrationRebuildRequest(
        string? ScopeId,
        string? RegistrationId,
        string? NyxAgentApiKeyId,
        string? Reason,
        bool Force);

    private sealed record RepairLarkMirrorRequest(
        string? RegistrationId,
        string? ScopeId,
        string? NyxProviderSlug,
        string? WebhookBaseUrl,
        string? NyxChannelBotId,
        string? NyxAgentApiKeyId,
        string? NyxConversationRouteId);

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
        // Telegram-specific shorthand: equivalent to Credentials["bot_token"].
        string? BotToken,
        // Platform-extensible credential bag. Per-platform provisioning services document
        // which keys they expect (e.g. Telegram reads "bot_token").
        IReadOnlyDictionary<string, string>? Credentials,
        string? Label);

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
