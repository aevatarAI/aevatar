using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.NyxidChat;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
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

        // Diagnostic: test reply path without going through full LLM chat
        group.MapPost("/registrations/{registrationId}/test-reply", HandleTestReplyAsync).RequireAuthorization();

        // Diagnostic: view recent actor errors (stored in IMemoryCache by ChannelUserGAgent)
        group.MapGet("/diagnostics/errors", HandleGetDiagnosticErrorsAsync).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Gets or creates the well-known ChannelBotRegistrationGAgent singleton actor.
    /// Lifecycle: created on first request, never destroyed (long-lived fact owner per CLAUDE.md).
    /// Thread safety: Orleans grain runtime guarantees single-activation, so concurrent
    /// CreateAsync calls from multiple requests safely converge to the same grain.
    /// </summary>
    private static async Task<IActor> GetOrCreateRegistrationActorAsync(IActorRuntime actorRuntime)
    {
        return await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
               ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(ChannelBotRegistrationGAgent.WellKnownId);
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
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] IEnumerable<IPlatformAdapter> adapters,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Callback");

        // Resolve registration from projection read model
        var registration = await queryPort.GetAsync(registrationId, ct);
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

        // Dedup: Lark retries up to 5x for unacknowledged webhooks.
        // Two-phase TTL: set short TTL (10s) immediately to block concurrent duplicates,
        // then extend to 5 minutes after successful dispatch. If dispatch fails, the
        // short TTL expires naturally so Lark's next retry (~30s later) gets processed.
        // Note: volatile — dedup state lost on restart. Phase 2 migrates to durable dedup.
        var cache = http.RequestServices.GetService<IMemoryCache>();
        string? dedupeKey = null;
        if (cache != null && !string.IsNullOrEmpty(inbound.MessageId))
        {
            dedupeKey = $"channel-dedup:{inbound.Platform}:{registration.Id}:{inbound.MessageId}";
            if (cache.TryGetValue(dedupeKey, out _))
            {
                logger.LogInformation("Duplicate webhook ignored: {DedupeKey}", dedupeKey);
                return Results.Ok(new { status = "deduplicated" });
            }

            // Short TTL blocks concurrent duplicates; extended after successful dispatch.
            cache.Set(dedupeKey, true, TimeSpan.FromSeconds(10));
        }

        // Dispatch to user actor. HandleEventAsync enqueues the event into the actor's
        // inbox. The user actor processes inbound → chat → LLM → reply asynchronously.
        try
        {
            await DispatchToUserActorAsync(inbound, registration, http.RequestServices);
            // Lark requires exactly HTTP 200 — any other status (including 202) is treated as failure.
            return Results.Ok(new { status = "accepted" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Channel inbound dispatch failed: platform={Platform}, registrationId={RegistrationId}",
                inbound.Platform, registration.Id);
            // Return 200 even on error — Lark treats non-200 as failure and retries.
            // The error is logged server-side; the dispatch will retry via Lark's retry mechanism.
            return Results.Ok(new { status = "dispatch_error", error = ex.Message });
        }
    }

    /// <summary>
    /// Dispatches the inbound message through the full processing pipeline:
    /// 1. Identity tracking → ChannelUserGAgent (stateful grain)
    /// 2. Chat dispatch + response collection → NyxIdChatGAgent (stateless coordination)
    /// 3. Reply → platform adapter via NyxID proxy
    ///
    /// Chat coordination runs in the HTTP/ThreadPool context (not inside a grain)
    /// to avoid Orleans grain scheduler deadlock when subscribing to another
    /// actor's stream. This matches the pattern used by NyxIdChatEndpoints.
    /// </summary>
    private static async Task DispatchToUserActorAsync(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        IServiceProvider services)
    {
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Callback");
        var actorRuntime = services.GetRequiredService<IActorRuntime>();
        var cache = services.GetService<IMemoryCache>();

        // 1. Identity tracking — dispatch to ChannelUserGAgent (stateful grain)
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

        var identityEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(inboundEvent),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = userActor.Id },
            },
        };

        await userActor.HandleEventAsync(identityEnvelope);

        if (cache != null && !string.IsNullOrEmpty(inbound.MessageId))
        {
            var dedupeKey = $"channel-dedup:{inbound.Platform}:{registration.Id}:{inbound.MessageId}";
            cache.Set(dedupeKey, true, TimeSpan.FromMinutes(5));
        }

        // 2. Chat dispatch + reply — runs in HTTP/ThreadPool context, NOT inside a grain.
        // Uses the same subscribe+wait pattern as NyxIdChatEndpoints.
        try
        {
            var replyText = await DispatchChatAndCollectAsync(inbound, registration, services);
            await SendPlatformReplyAsync(replyText, inbound, registration, services);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Channel chat+reply failed: platform={Platform}", inbound.Platform);
            try
            {
                await SendPlatformReplyAsync(
                    $"[Error] {ex.GetType().Name}: {ex.Message}", inbound, registration, services);
            }
            catch (Exception replyEx)
            {
                logger.LogError(replyEx,
                    "Error reply also failed: platform={Platform}, originalError={OriginalError}",
                    inbound.Platform, ex.Message);
            }
        }
    }

    /// <summary>
    /// Creates/gets the NyxIdChatGAgent, subscribes to its stream, dispatches
    /// the ChatRequestEvent, and waits for the response. Runs entirely in
    /// HTTP/ThreadPool context — no grain scheduler involvement.
    /// </summary>
    private static async Task<string> DispatchChatAndCollectAsync(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Aevatar.ChannelRuntime.Chat");
        var actorRuntime = services.GetRequiredService<IActorRuntime>();
        var subscriptionProvider = services.GetRequiredService<IActorEventSubscriptionProvider>();
        var effectiveToken = registration.NyxUserToken;

        var chatActorId = $"channel-{inbound.Platform}-{registration.Id}-{inbound.SenderId}";
        var chatActor = await actorRuntime.GetAsync(chatActorId)
                        ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(chatActorId);

        var sessionId = Guid.NewGuid().ToString("N");
        var chatRequest = new ChatRequestEvent
        {
            Prompt = inbound.Text,
            SessionId = sessionId,
            ScopeId = registration.ScopeId,
        };
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = effectiveToken;
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdOrgToken] = registration.NyxUserToken;
        chatRequest.Metadata["scope_id"] = registration.ScopeId;
        chatRequest.Metadata[ChannelMetadataKeys.Platform] = inbound.Platform;
        chatRequest.Metadata[ChannelMetadataKeys.SenderId] = inbound.SenderId;
        chatRequest.Metadata[ChannelMetadataKeys.SenderName] = inbound.SenderName;
        chatRequest.Metadata[ChannelMetadataKeys.MessageId] = inbound.MessageId ?? string.Empty;
        chatRequest.Metadata[ChannelMetadataKeys.ChatType] = inbound.ChatType ?? string.Empty;

        var responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseBuilder = new StringBuilder();

        await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
            chatActor.Id,
            envelope =>
            {
                var payload = envelope.Payload;
                if (payload is null) return Task.CompletedTask;

                if (payload.Is(TextMessageContentEvent.Descriptor))
                {
                    var contentEvt = payload.Unpack<TextMessageContentEvent>();
                    if (contentEvt.SessionId == sessionId && !string.IsNullOrEmpty(contentEvt.Delta))
                        responseBuilder.Append(contentEvt.Delta);
                }
                else if (payload.Is(TextMessageEndEvent.Descriptor))
                {
                    var endEvt = payload.Unpack<TextMessageEndEvent>();
                    if (endEvt.SessionId == sessionId)
                        responseTcs.TrySetResult(responseBuilder.ToString());
                }

                return Task.CompletedTask;
            });

        var chatEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(chatRequest),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = chatActor.Id },
            },
        };

        await chatActor.HandleEventAsync(chatEnvelope);

        var timeoutTask = Task.Delay(120_000);
        var completed = await Task.WhenAny(responseTcs.Task, timeoutTask);

        if (completed == responseTcs.Task && responseTcs.Task.IsCompletedSuccessfully)
        {
            var text = responseTcs.Task.Result;
            logger.LogInformation("Channel response ready: length={Length}", text.Length);
            return text;
        }

        var partial = responseBuilder.ToString();
        logger.LogWarning("Channel response timed out");
        return partial.Length > 0
            ? partial
            : "Sorry, it's taking too long to respond. Please try again.";
    }

    private static async Task SendPlatformReplyAsync(
        string replyText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        IServiceProvider services)
    {
        if (string.IsNullOrWhiteSpace(replyText))
            replyText = "Sorry, I wasn't able to generate a response. Please try again.";

        var adapters = services.GetRequiredService<IEnumerable<IPlatformAdapter>>();
        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, inbound.Platform, StringComparison.OrdinalIgnoreCase));

        if (adapter is null) return;

        var nyxClient = services.GetRequiredService<NyxIdApiClient>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await adapter.SendReplyAsync(replyText, inbound, registration, nyxClient, cts.Token);
    }

    // ─── Registration CRUD ───

    private static readonly JsonSerializerOptions RegistrationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
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
            webhookUrl = baseUrl + callbackPath;
        }

        // Ensure projection scope is activated BEFORE dispatch — without this,
        // the scope agent never subscribes and the projector never runs.
        var projectionPort = http.RequestServices.GetService<ChannelBotRegistrationProjectionPort>();
        if (projectionPort != null)
            await projectionPort.EnsureProjectionForActorAsync(ChannelBotRegistrationGAgent.WellKnownId, ct);

        var registrationId = Guid.NewGuid().ToString("N");

        // Dispatch register command to actor
        var actor = await GetOrCreateRegistrationActorAsync(actorRuntime);
        var cmd = new ChannelBotRegisterCommand
        {
            Platform = platformNormalized,
            NyxProviderSlug = request.NyxProviderSlug.Trim(),
            NyxUserToken = request.NyxUserToken.Trim(),
            VerificationToken = request.VerificationToken?.Trim() ?? string.Empty,
            ScopeId = request.ScopeId?.Trim() ?? string.Empty,
            WebhookUrl = webhookUrl ?? string.Empty,
            RequestedId = registrationId,
        };

        var cmdEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(cmd),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(cmdEnvelope);

        return Results.Accepted(value: new
        {
            status = "registered",
            registration_id = registrationId,
            platform = platformNormalized,
            nyx_provider_slug = request.NyxProviderSlug.Trim(),
            callback_url = $"{callbackPath}/{registrationId}",
        });
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
            nyx_provider_slug = e.NyxProviderSlug,
            scope_id = e.ScopeId,
            callback_url = $"/api/channels/{e.Platform}/callback/{e.Id}",
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> HandleDeleteRegistrationAsync(
        string registrationId,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        CancellationToken ct)
    {
        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return Results.NotFound(new { error = "Registration not found" });

        var actor = await GetOrCreateRegistrationActorAsync(actorRuntime);
        var cmd = new ChannelBotUnregisterCommand { RegistrationId = registrationId };
        var cmdEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(cmd),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(cmdEnvelope);
        return Results.Ok(new { status = "deleted" });
    }

    /// <summary>
    /// Diagnostic: sends a test reply directly through the platform adapter,
    /// bypassing the full LLM chat flow. Isolates whether the reply path
    /// (NyxID proxy → platform API) is working.
    /// </summary>
    private static async Task<IResult> HandleTestReplyAsync(
        HttpContext http,
        string registrationId,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] IEnumerable<IPlatformAdapter> adapters,
        [FromServices] NyxIdApiClient nyxClient,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Diagnostic");

        var registration = await queryPort.GetAsync(registrationId, ct);
        if (registration is null)
            return Results.NotFound(new { error = "Registration not found" });

        // Read optional test parameters from body
        TestReplyRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<TestReplyRequest>(RegistrationJsonOptions, ct);
        }
        catch
        {
            request = null;
        }

        var chatId = request?.ChatId;
        var message = request?.Message ?? "[Aevatar Test] Reply path is working.";

        if (string.IsNullOrWhiteSpace(chatId))
            return Results.BadRequest(new
            {
                error = "chat_id is required",
                hint = "Send { \"chat_id\": \"oc_xxx\", \"message\": \"hello\" }. " +
                       "Get chat_id from Lark webhook payload event.message.chat_id.",
            });

        // Diagnostic: show what we're about to send
        var diagnostics = new
        {
            registration_id = registration.Id,
            platform = registration.Platform,
            nyx_provider_slug = registration.NyxProviderSlug,
            nyx_user_token_present = !string.IsNullOrWhiteSpace(registration.NyxUserToken),
            nyx_user_token_length = registration.NyxUserToken?.Length ?? 0,
            scope_id = registration.ScopeId,
            target_chat_id = chatId,
        };

        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, registration.Platform, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
            return Results.BadRequest(new { error = $"No adapter for platform: {registration.Platform}", diagnostics });

        var inbound = new InboundMessage
        {
            Platform = registration.Platform,
            ConversationId = chatId,
            SenderId = "test-diagnostic",
            SenderName = "test-diagnostic",
            Text = "test",
            ChatType = "p2p",
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await adapter.SendReplyAsync(message, inbound, registration, nyxClient, cts.Token);
            return Results.Ok(new { status = "sent", diagnostics, message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Test reply failed for registration {RegistrationId}", registrationId);
            return Results.Json(new
            {
                status = "error",
                error = ex.Message,
                error_type = ex.GetType().Name,
                diagnostics,
            }, statusCode: 500);
        }
    }

    /// <summary>
    /// Returns recent actor errors from memory cache.
    /// ChannelUserGAgent stores errors under "channel-diag:errors" key.
    /// </summary>
    private static Task<IResult> HandleGetDiagnosticErrorsAsync(
        [FromServices] IMemoryCache? cache)
    {
        if (cache == null)
            return Task.FromResult(Results.Ok(new { errors = Array.Empty<object>() }));

        var errors = cache.Get<List<object>>(ChannelDiagnosticKeys.RecentErrors)
                    ?? new List<object>();
        return Task.FromResult(Results.Ok(new { count = errors.Count, errors }));
    }

    private sealed record RegistrationRequest(
        string? Platform,
        string? NyxProviderSlug,
        string? NyxUserToken,
        string? VerificationToken,
        string? ScopeId,
        string? WebhookBaseUrl);

    private sealed record TestReplyRequest(string? ChatId, string? Message);
}

/// <summary>Shared keys for diagnostic error cache.</summary>
public static class ChannelDiagnosticKeys
{
    public const string RecentErrors = "channel-diag:errors";
}
