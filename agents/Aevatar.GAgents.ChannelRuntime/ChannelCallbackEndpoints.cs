using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
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

        // Platform callback — receives webhooks directly from platforms (anonymous: platforms call this)
        group.MapPost("/{platform}/callback/{registrationId}", HandleCallbackAsync);

        // Registration CRUD — requires authentication
        group.MapPost("/registrations", HandleRegisterAsync).RequireAuthorization();
        group.MapGet("/registrations", HandleListRegistrationsAsync).RequireAuthorization();
        group.MapDelete("/registrations/{registrationId}", HandleDeleteRegistrationAsync).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Receives a platform webhook callback directly.
    /// 1. Handles verification challenges (returns immediately).
    /// 2. Parses inbound message.
    /// 3. Returns 200 OK immediately (platforms have short timeouts).
    /// 4. Fires background task: dispatch to actor, collect response, send reply via Nyx provider.
    /// </summary>
    private static async Task<IResult> HandleCallbackAsync(
        HttpContext http,
        string platform,
        string registrationId,
        [FromServices] ChannelBotRegistrationStore registrationStore,
        [FromServices] IEnumerable<IPlatformAdapter> adapters,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Callback");

        // Resolve registration
        var registration = registrationStore.Get(registrationId);
        if (registration is null)
        {
            logger.LogWarning("Channel callback for unknown registration: {RegistrationId}", registrationId);
            return Results.NotFound(new { error = "Registration not found" });
        }

        if (!string.Equals(registration.Platform, platform, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "Platform mismatch" });
        }

        // Resolve adapter
        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            logger.LogWarning("No adapter for platform: {Platform}", platform);
            return Results.BadRequest(new { error = $"Unsupported platform: {platform}" });
        }

        // Handle verification challenges (e.g. Lark URL verification)
        http.Request.EnableBuffering();
        var verificationResult = await adapter.TryHandleVerificationAsync(http, registration);
        if (verificationResult is not null)
            return verificationResult;

        // Parse inbound message
        var inbound = await adapter.ParseInboundAsync(http, registration);
        if (inbound is null)
        {
            // Not a processable message (e.g. unsupported event type) — acknowledge silently
            return Results.Ok(new { status = "ignored" });
        }

        // Return 200 OK immediately — process async in background.
        // Platforms like Lark have ~3s webhook timeout; we can't wait for LLM response.
        _ = Task.Run(() => DispatchToUserActorAsync(
            inbound, registration, actorRuntime, loggerFactory));

        return Results.Ok(new { status = "accepted" });
    }

    /// <summary>
    /// Background task: dispatch inbound message to ChannelUserGAgent.
    /// The user actor handles the full flow: identity tracking → chat → reply.
    /// </summary>
    private static async Task DispatchToUserActorAsync(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        IActorRuntime actorRuntime,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Callback");

        try
        {
            // Per-sender user actor — owns identity state and orchestrates chat + reply
            var userActorId = $"channel-user-{inbound.Platform}-{registration.Id}-{inbound.SenderId}";
            var userActor = await actorRuntime.GetAsync(userActorId)
                            ?? await actorRuntime.CreateAsync<ChannelUserGAgent>(userActorId);

            var inboundEvent = new ChannelInboundEvent
            {
                Text = inbound.Text,
                SenderId = inbound.SenderId,
                SenderName = inbound.SenderName,
                ConversationId = inbound.ConversationId,
                MessageId = inbound.MessageId ?? string.Empty,
                ChatType = inbound.ChatType ?? string.Empty,
                Platform = inbound.Platform,
                RegistrationId = registration.Id,
                RegistrationToken = registration.NyxUserToken,
                RegistrationScopeId = registration.ScopeId,
                NyxProviderSlug = registration.NyxProviderSlug,
            };

            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Google.Protobuf.WellKnownTypes.Any.Pack(inboundEvent),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = userActor.Id },
                },
            };

            await userActor.HandleEventAsync(envelope);

            logger.LogInformation(
                "Channel inbound dispatched: platform={Platform}, sender={SenderId}",
                inbound.Platform, inbound.SenderId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Channel callback dispatch failed: platform={Platform}, sender={SenderId}",
                inbound.Platform, inbound.SenderId);
        }
    }

    // ─── Registration CRUD ───

    private static readonly JsonSerializerOptions RegistrationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext http,
        [FromServices] ChannelBotRegistrationStore store,
        [FromServices] IEnumerable<IPlatformAdapter> adapters,
        [FromServices] NyxIdApiClient nyxClient,
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
            string.IsNullOrWhiteSpace(request.Platform) ||
            string.IsNullOrWhiteSpace(request.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(request.NyxUserToken))
        {
            return Results.BadRequest(new { error = "platform, nyx_provider_slug, and nyx_user_token are required" });
        }

        // Validate platform has a registered adapter
        var platformNormalized = request.Platform.Trim().ToLowerInvariant();
        var hasAdapter = adapters.Any(a =>
            string.Equals(a.Platform, platformNormalized, StringComparison.OrdinalIgnoreCase));
        if (!hasAdapter)
        {
            var supported = string.Join(", ", adapters.Select(a => a.Platform));
            return Results.BadRequest(new { error = $"Unsupported platform: '{platformNormalized}'. Supported: {supported}" });
        }

        // Build callback URL for webhook configuration
        var callbackPath = $"/api/channels/{request.Platform.Trim().ToLowerInvariant()}/callback";
        string? webhookUrl = null;
        if (!string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
        {
            var baseUrl = request.WebhookBaseUrl.Trim().TrimEnd('/');
            // Will be completed with /{registrationId} after registration
            webhookUrl = baseUrl + callbackPath;
        }

        var entry = store.Register(
            request.Platform.Trim().ToLowerInvariant(),
            request.NyxProviderSlug.Trim(),
            request.NyxUserToken.Trim(),
            request.VerificationToken?.Trim(),
            request.ScopeId?.Trim(),
            webhookUrl: webhookUrl != null ? $"{webhookUrl}/{null}" : null);

        // Complete the webhook URL with the registration ID
        var fullCallbackUrl = $"{callbackPath}/{entry.Id}";
        if (webhookUrl != null)
        {
            var fullWebhookUrl = $"{webhookUrl}/{entry.Id}";

            // Auto-configure platform webhook via NyxID proxy
            try
            {
                await ConfigureWebhookAsync(
                    entry, fullWebhookUrl, nyxClient, logger, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to auto-configure webhook for {Platform} registration {Id}. " +
                    "Webhook can be configured manually later.",
                    entry.Platform, entry.Id);
            }
        }

        return Results.Ok(new
        {
            id = entry.Id,
            platform = entry.Platform,
            nyx_provider_slug = entry.NyxProviderSlug,
            callback_url = fullCallbackUrl,
            webhook_url = webhookUrl != null ? $"{webhookUrl}/{entry.Id}" : (string?)null,
            created_at = entry.CreatedAt.ToDateTimeOffset(),
        });
    }

    private static Task<IResult> HandleListRegistrationsAsync(
        [FromServices] ChannelBotRegistrationStore store)
    {
        var registrations = store.List().Select(e => new
        {
            id = e.Id,
            platform = e.Platform,
            nyx_provider_slug = e.NyxProviderSlug,
            scope_id = e.ScopeId,
            callback_url = $"/api/channels/{e.Platform}/callback/{e.Id}",
            created_at = e.CreatedAt.ToDateTimeOffset(),
        });

        return Task.FromResult(Results.Ok(registrations));
    }

    private static Task<IResult> HandleDeleteRegistrationAsync(
        string registrationId,
        [FromServices] ChannelBotRegistrationStore store)
    {
        var deleted = store.Delete(registrationId);
        return Task.FromResult(deleted
            ? Results.Ok(new { status = "deleted" })
            : Results.NotFound(new { error = "Registration not found" }));
    }

    /// <summary>
    /// Auto-configure platform webhook via NyxID proxy.
    /// For Telegram: calls setWebhook to register the callback URL.
    /// </summary>
    private static async Task ConfigureWebhookAsync(
        ChannelBotRegistrationEntry entry,
        string webhookUrl,
        NyxIdApiClient nyxClient,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.Equals(entry.Platform, "telegram", StringComparison.OrdinalIgnoreCase))
        {
            // Call Telegram Bot API setWebhook via NyxID proxy.
            // NyxID proxy auto-prepends "bot<TOKEN>/" so we pass the bare method name.
            var body = JsonSerializer.Serialize(new { url = webhookUrl });
            var result = await nyxClient.ProxyRequestAsync(
                entry.NyxUserToken,
                entry.NyxProviderSlug,
                "setWebhook",
                "POST",
                body,
                extraHeaders: null,
                ct);

            logger.LogInformation(
                "Telegram setWebhook configured: registration={Id}, url={Url}, result={Result}",
                entry.Id, webhookUrl, result?.Length > 200 ? result[..200] : result);
        }
        else
        {
            logger.LogDebug(
                "No auto-webhook configuration for platform {Platform}", entry.Platform);
        }
    }

    private sealed record RegistrationRequest(
        string? Platform,
        string? NyxProviderSlug,
        string? NyxUserToken,
        string? VerificationToken,
        string? ScopeId,
        string? WebhookBaseUrl);
}
