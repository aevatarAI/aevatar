using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

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

        var accessToken = http.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (accessToken.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            accessToken = accessToken[bearerPrefix.Length..].Trim();

        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
        {
            return Results.BadRequest(new { error = "webhook_base_url is required for Nyx-backed relay provisioning" });
        }

        var result = await provisioningService.ProvisionAsync(
            new NyxChannelBotProvisioningRequest(
                Platform: platformNormalized,
                AccessToken: accessToken,
                WebhookBaseUrl: request.WebhookBaseUrl.Trim(),
                ScopeId: request.ScopeId?.Trim() ?? string.Empty,
                Label: request.Label?.Trim() ?? string.Empty,
                NyxProviderSlug: request.NyxProviderSlug?.Trim() ?? string.Empty,
                Lark: new NyxChannelLarkCredentials(
                    AppId: request.AppId?.Trim() ?? string.Empty,
                    AppSecret: request.AppSecret?.Trim() ?? string.Empty,
                    VerificationToken: request.VerificationToken?.Trim() ?? string.Empty)),
            ct);

        var payload = new
        {
            status = result.Status,
            registration_id = result.RegistrationId ?? string.Empty,
            platform = result.Platform,
            nyx_provider_slug = string.IsNullOrWhiteSpace(request.NyxProviderSlug)
                ? "api-lark-bot"
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
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort dispatchPort,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Registration");
        await ChannelBotRegistrationStoreCommands.DispatchRebuildProjectionAsync(
            actorRuntime,
            dispatchPort,
            "http_api_manual_rebuild",
            ct);

        int? observedRegistrationsBeforeRebuild = null;
        var note = "Projection rebuild dispatched from authoritative channel-bot-registration-store state. Query-side registrations may take a moment to refresh.";
        try
        {
            observedRegistrationsBeforeRebuild = (await queryPort.QueryAllAsync(ct)).Count;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Channel registration query failed after dispatching a manual rebuild");
            note = "Projection rebuild dispatched from authoritative channel-bot-registration-store state. Query-side observation is currently unavailable; registrations may still refresh asynchronously.";
        }

        return Results.Accepted(value: new
        {
            status = "accepted",
            actor_id = ChannelBotRegistrationGAgent.WellKnownId,
            observed_registrations_before_rebuild = observedRegistrationsBeforeRebuild,
            note,
        });
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
            "missing_app_id" or "missing_app_secret" or "missing_verification_token" or "missing_webhook_base_url" => StatusCodes.Status400BadRequest,
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

    private sealed record RegistrationRequest(
        string? Platform,
        string? NyxProviderSlug,
        string? ScopeId,
        string? WebhookBaseUrl,
        string? AppId,
        string? AppSecret,
        string? VerificationToken,
        string? Label);
}
