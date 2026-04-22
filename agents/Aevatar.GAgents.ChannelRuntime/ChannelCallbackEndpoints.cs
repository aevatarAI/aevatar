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

        // Token refresh — update the NyxID access token on an existing registration
        group.MapPatch("/registrations/{registrationId}/token", HandleUpdateTokenAsync).RequireAuthorization();

        // Diagnostic: test reply path without going through full LLM chat
        group.MapPost("/registrations/{registrationId}/test-reply", HandleTestReplyAsync).RequireAuthorization();
        group.MapGet("/diagnostics/errors", HandleGetDiagnosticErrorsAsync);

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
        [FromServices] IChannelBotRegistrationRuntimeQueryPort runtimeQueryPort,
        [FromServices] LarkDirectWebhookCutoverOptions larkCutoverOptions,
        [FromServices] IEnumerable<IPlatformAdapter> adapters,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] ILarkConversationIngressRuntime larkIngressRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Callback");
        var diagnostics = http.RequestServices.GetService<IChannelRuntimeDiagnostics>();

        // Resolve registration from projection read model
        var registration = await runtimeQueryPort.GetAsync(registrationId, ct);
        if (registration is null)
        {
            logger.LogWarning("Channel callback for unknown registration: {RegistrationId}", registrationId);
            RecordDiagnostic(diagnostics, "Callback:error", platform, registrationId, "registration_not_found");
            return Results.NotFound(new { error = "Registration not found" });
        }

        if (!string.Equals(registration.Platform, platform, StringComparison.OrdinalIgnoreCase))
        {
            RecordDiagnostic(diagnostics, "Callback:error", platform, registrationId, "platform_mismatch");
            return Results.BadRequest(new { error = "Platform mismatch" });
        }

        if (!ShouldAcceptDirectLarkCallback(registration, larkCutoverOptions, DateTimeOffset.UtcNow))
        {
            RecordDiagnostic(
                diagnostics,
                "Callback:retired",
                registration.Platform,
                registration.Id,
                "lark_direct_callback_retired");
            return Results.Json(
                new
                {
                    error = "Lark direct callback is retired. Point the Lark Developer Console callback URL to the NyxID webhook URL and use Nyx relay ingress instead.",
                    registration_id = registration.Id,
                    platform = registration.Platform,
                },
                statusCode: StatusCodes.Status410Gone);
        }

        if (string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase))
            return await larkIngressRuntime.HandleAsync(http, registration, ct);

        // Resolve adapter
        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            logger.LogWarning("No adapter for platform: {Platform}", platform);
            RecordDiagnostic(diagnostics, "Callback:error", platform, registrationId, "adapter_not_found");
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
            RecordDiagnostic(diagnostics, "Callback:ignored", platform, registration.Id, "adapter_returned_null");
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
                RecordDiagnostic(diagnostics, "Callback:deduplicated", inbound.Platform, registration.Id, "webhook_duplicate");
                return Results.Ok(new { status = "deduplicated" });
            }

            // Short TTL blocks concurrent duplicates; extended after successful dispatch.
            cache.Set(dedupeKey, true, TimeSpan.FromSeconds(10));
        }

        if (ChannelWorkflowTextRouting.TryBuildWorkflowResumeCommand(inbound, out var resumeCommand) ||
            ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out resumeCommand))
        {
            var resumeService = http.RequestServices
                .GetService<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
            if (resumeService is null)
            {
                logger.LogError(
                    "Workflow resume service unavailable for card action: registrationId={RegistrationId}",
                    registration.Id);
                RecordDiagnostic(diagnostics, "Callback:error", inbound.Platform, registration.Id, "workflow_resume_service_unavailable");
                return Results.Json(
                    new { status = "workflow_resume_service_unavailable" },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var dispatch = await resumeService.DispatchAsync(resumeCommand!, ct);
            if (!dispatch.Succeeded || dispatch.Receipt is null)
            {
                var failure = MapWorkflowResumeDispatchFailure(dispatch.Error, inbound, registration.Id, diagnostics);
                return failure;
            }

            ExtendDedupeTtl(cache, inbound, registration.Id);
            RecordDiagnostic(diagnostics, "Callback:workflow_resume", inbound.Platform, registration.Id,
                $"actorId={dispatch.Receipt.ActorId} runId={dispatch.Receipt.RunId}");
            return Results.Ok(new
            {
                status = "resume_dispatched",
                actorId = dispatch.Receipt.ActorId,
                runId = dispatch.Receipt.RunId,
                commandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
            });
        }

        // Dispatch to ChannelUserGAgent. HandleEventAsync enqueues the event into the
        // actor's inbox (stream publish) and returns immediately. The actor handles the
        // full continuation flow: identity tracking → chat dispatch → stream forwarding →
        // response collection → reply. Each stage is a separate grain turn — no deadlock.
        try
        {
            await DispatchToUserActorAsync(inbound, registration, actorRuntime, cache);
            RecordDiagnostic(diagnostics, "Callback:accepted", inbound.Platform, registration.Id, "dispatch_enqueued");
            // Lark requires exactly HTTP 200 — any other status is treated as failure.
            return Results.Ok(new { status = "accepted" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Channel inbound dispatch failed: platform={Platform}, registrationId={RegistrationId}",
                inbound.Platform, registration.Id);
            RecordDiagnostic(diagnostics, "Callback:error", inbound.Platform, registration.Id,
                $"{ex.GetType().Name}: {ex.Message}");
            // Return 500 so webhook providers (Lark/Telegram) retry the delivery
            // instead of treating the message as successfully processed.
            return Results.Json(
                new { status = "dispatch_error", error = ex.Message },
                statusCode: 500);
        }
    }

    /// <summary>
    /// Dispatches inbound message to ChannelUserGAgent via stream publish.
    /// Returns immediately — the actor handles the full continuation flow
    /// (identity tracking → chat dispatch → response collection → reply)
    /// across multiple grain turns.
    /// </summary>
    private static async Task DispatchToUserActorAsync(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        IActorRuntime actorRuntime,
        IMemoryCache? cache)
    {
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
            RegistrationToken = registration.LegacyDirectBinding?.NyxUserToken ?? string.Empty,
            RegistrationScopeId = registration.ScopeId,
            NyxProviderSlug = registration.NyxProviderSlug,
        };
        foreach (var pair in inbound.Extra)
            inboundEvent.Extra[pair.Key] = pair.Value;

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

        ExtendDedupeTtl(cache, inbound, registration.Id);
    }

    private static void ExtendDedupeTtl(IMemoryCache? cache, InboundMessage inbound, string registrationId)
    {
        if (cache == null || string.IsNullOrEmpty(inbound.MessageId))
            return;

        var dedupeKey = $"channel-dedup:{inbound.Platform}:{registrationId}:{inbound.MessageId}";
        cache.Set(dedupeKey, true, TimeSpan.FromMinutes(5));
    }

    private static IResult MapWorkflowResumeDispatchFailure(
        WorkflowRunControlStartError error,
        InboundMessage inbound,
        string registrationId,
        IChannelRuntimeDiagnostics? diagnostics)
    {
        var (statusCode, message) = error.Code switch
        {
            WorkflowRunControlStartErrorCode.InvalidActorId => (
                StatusCodes.Status400BadRequest,
                "actorId is required."),
            WorkflowRunControlStartErrorCode.InvalidRunId => (
                StatusCodes.Status400BadRequest,
                "runId is required."),
            WorkflowRunControlStartErrorCode.InvalidStepId => (
                StatusCodes.Status400BadRequest,
                "stepId is required."),
            WorkflowRunControlStartErrorCode.ActorNotFound => (
                StatusCodes.Status404NotFound,
                $"Actor '{error.ActorId}' not found."),
            WorkflowRunControlStartErrorCode.ActorNotWorkflowRun => (
                StatusCodes.Status400BadRequest,
                $"Actor '{error.ActorId}' is not a workflow run actor."),
            WorkflowRunControlStartErrorCode.RunBindingMissing => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' does not have a bound run id."),
            WorkflowRunControlStartErrorCode.RunBindingMismatch => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' is bound to run '{error.BoundRunId}', not '{error.RequestedRunId}'."),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Workflow control dispatch failed."),
        };

        RecordDiagnostic(diagnostics, "Callback:error", inbound.Platform, registrationId,
            $"workflow_resume_failed:{error.Code}");
        return Results.Json(new { error = message }, statusCode: statusCode);
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
        if (string.Equals(platformNormalized, "lark", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new
            {
                error = "Direct Lark registration is retired. Use the Nyx-backed provisioning flow so the webhook URL points to NyxID instead of Aevatar.",
            });
        }

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

        var registrationId = Guid.NewGuid().ToString("N");

        // Dispatch register command to actor
        var actor = await GetOrCreateRegistrationActorAsync(actorRuntime);
        var cmd = new ChannelBotRegisterCommand
        {
            Platform = platformNormalized,
            NyxProviderSlug = request.NyxProviderSlug.Trim(),
            ScopeId = request.ScopeId?.Trim() ?? string.Empty,
            WebhookUrl = webhookUrl ?? string.Empty,
            RequestedId = registrationId,
            LegacyDirectBinding = BuildLegacyDirectBinding(
                request.NyxUserToken,
                request.NyxRefreshToken,
                request.VerificationToken,
                request.CredentialRef),
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
            status = "accepted",
            registration_id = registrationId,
            platform = platformNormalized,
            nyx_provider_slug = request.NyxProviderSlug.Trim(),
            callback_url = $"{callbackPath}/{registrationId}",
            auto_refresh_ready = !string.IsNullOrWhiteSpace(cmd.LegacyDirectBinding?.NyxRefreshToken),
            note = "Registration accepted. Read model visibility is asynchronous; list/query results may lag briefly until the projection pipeline catches up.",
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
            registration_mode = string.IsNullOrWhiteSpace(e.NyxAgentApiKeyId) ? "legacy_direct_callback" : "nyx_relay_webhook",
            nyx_provider_slug = e.NyxProviderSlug,
            scope_id = e.ScopeId,
            callback_url = string.IsNullOrWhiteSpace(e.NyxAgentApiKeyId)
                ? $"/api/channels/{e.Platform}/callback/{e.Id}"
                : string.Empty,
            nyx_channel_bot_id = e.NyxChannelBotId,
            nyx_agent_api_key_id = e.NyxAgentApiKeyId,
            nyx_conversation_route_id = e.NyxConversationRouteId,
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

    private static async Task<IResult> HandleUpdateTokenAsync(
        string registrationId,
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] IChannelBotRegistrationRuntimeQueryPort runtimeQueryPort,
        [FromServices] LarkDirectWebhookCutoverOptions larkCutoverOptions,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Registration");

        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return Results.NotFound(new { error = "Registration not found" });

        if (string.Equals(exists.Platform, "lark", StringComparison.OrdinalIgnoreCase) &&
            !ShouldAcceptLegacyDirectLarkOperation(exists, larkCutoverOptions, DateTimeOffset.UtcNow))
        {
            return Results.Conflict(new
            {
                error = "Lark registrations on the Nyx relay path do not use persisted Nyx session tokens. Re-provision through the Nyx-backed Lark flow instead of updating tokens here.",
                registration_id = registrationId,
            });
        }

        UpdateTokenRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<UpdateTokenRequest>(RegistrationJsonOptions, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid update-token request payload");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.NyxUserToken))
            return Results.BadRequest(new { error = "nyx_user_token is required" });

        var newToken = request.NyxUserToken.Trim();
        var runtimeRegistration = await runtimeQueryPort.GetAsync(registrationId, ct) ?? exists;
        var currentLegacyDirectBinding = runtimeRegistration.LegacyDirectBinding;
        var newRefreshToken = ResolveUpdatedRefreshToken(
            request.NyxRefreshToken,
            currentLegacyDirectBinding?.NyxRefreshToken);

        // Always dispatch to the actor — it is the authority on current state.
        var actor = await GetOrCreateRegistrationActorAsync(actorRuntime);
        var cmd = new ChannelBotUpdateTokenCommand
        {
            RegistrationId = registrationId,
            LegacyDirectBinding = MergeLegacyDirectBinding(
                currentLegacyDirectBinding,
                newToken,
                newRefreshToken),
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
            status = "accepted",
            registration_id = registrationId,
            auto_refresh_ready = !string.IsNullOrWhiteSpace(newRefreshToken),
            note = "Token update accepted. Read model visibility is asynchronous; query/list results may lag briefly until the projection pipeline catches up.",
        });
    }

    internal static string ResolveUpdatedRefreshToken(string? requestedRefreshToken, string? existingRefreshToken)
    {
        if (requestedRefreshToken is null)
            return existingRefreshToken?.Trim() ?? string.Empty;

        return requestedRefreshToken.Trim();
    }

    internal static bool ShouldAcceptDirectLarkCallback(
        ChannelBotRegistrationEntry registration,
        LarkDirectWebhookCutoverOptions options,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(options);

        if (!string.Equals(registration.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(registration.NyxAgentApiKeyId))
            return false;

        return options.AllowsLegacyDirectCallbackAt(nowUtc);
    }

    internal static bool ShouldAcceptLegacyDirectLarkOperation(
        ChannelBotRegistrationEntry registration,
        LarkDirectWebhookCutoverOptions options,
        DateTimeOffset nowUtc) =>
        ShouldAcceptDirectLarkCallback(registration, options, nowUtc);

    private static ChannelBotLegacyDirectBinding? BuildLegacyDirectBinding(
        string? nyxUserToken,
        string? nyxRefreshToken,
        string? verificationToken,
        string? credentialRef)
    {
        var userToken = nyxUserToken?.Trim() ?? string.Empty;
        var refreshToken = nyxRefreshToken?.Trim() ?? string.Empty;
        var verifyToken = verificationToken?.Trim() ?? string.Empty;
        var secretRef = credentialRef?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userToken) &&
            string.IsNullOrWhiteSpace(refreshToken) &&
            string.IsNullOrWhiteSpace(verifyToken) &&
            string.IsNullOrWhiteSpace(secretRef))
        {
            return null;
        }

        return new ChannelBotLegacyDirectBinding
        {
            NyxUserToken = userToken,
            NyxRefreshToken = refreshToken,
            VerificationToken = verifyToken,
            CredentialRef = secretRef,
            EncryptKey = string.Empty,
        };
    }

    private static ChannelBotLegacyDirectBinding MergeLegacyDirectBinding(
        ChannelBotLegacyDirectBinding? existing,
        string userToken,
        string refreshToken) =>
        new()
        {
            NyxUserToken = userToken,
            NyxRefreshToken = refreshToken,
            VerificationToken = existing?.VerificationToken ?? string.Empty,
            CredentialRef = existing?.CredentialRef ?? string.Empty,
            EncryptKey = existing?.EncryptKey ?? string.Empty,
        };

    /// <summary>
    /// Diagnostic: sends a test reply directly through the platform adapter,
    /// bypassing the full LLM chat flow. Isolates whether the reply path
    /// (NyxID proxy → platform API) is working.
    /// </summary>
    private static async Task<IResult> HandleTestReplyAsync(
        HttpContext http,
        string registrationId,
        [FromServices] IChannelBotRegistrationRuntimeQueryPort runtimeQueryPort,
        [FromServices] LarkDirectWebhookCutoverOptions larkCutoverOptions,
        [FromServices] IEnumerable<IPlatformAdapter> adapters,
        [FromServices] NyxIdApiClient nyxClient,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Diagnostic");

        var registration = await runtimeQueryPort.GetAsync(registrationId, ct);
        if (registration is null)
            return Results.NotFound(new { error = "Registration not found" });

        if (string.Equals(registration.Platform, "lark", StringComparison.OrdinalIgnoreCase) &&
            !ShouldAcceptLegacyDirectLarkOperation(registration, larkCutoverOptions, DateTimeOffset.UtcNow))
        {
            return Results.Conflict(new
            {
                error = "Direct Lark test replies are retired on the Nyx relay path. Validate the relay callback and channel-relay/reply flow instead.",
                registration_id = registrationId,
            });
        }

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
            nyx_user_token_present = !string.IsNullOrWhiteSpace(registration.LegacyDirectBinding?.NyxUserToken),
            nyx_user_token_length = registration.LegacyDirectBinding?.NyxUserToken?.Length ?? 0,
            nyx_refresh_token_present = !string.IsNullOrWhiteSpace(registration.LegacyDirectBinding?.NyxRefreshToken),
            nyx_refresh_token_length = registration.LegacyDirectBinding?.NyxRefreshToken?.Length ?? 0,
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
            var replyService = http.RequestServices.GetService<ChannelPlatformReplyService>();
            var delivery = replyService is not null
                ? await replyService.DeliverAsync(adapter, message, inbound, registration, cts.Token)
                : await adapter.SendReplyAsync(message, inbound, registration, nyxClient, cts.Token);
            if (!delivery.Succeeded)
            {
                return Results.Json(new
                {
                    status = "error",
                    error = delivery.Detail ?? "Reply delivery failed.",
                    diagnostics,
                    message,
                }, statusCode: 500);
            }

            return Results.Ok(new { status = "sent", diagnostics, message, detail = delivery.Detail });
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

    private sealed record RegistrationRequest(
        string? Platform,
        string? NyxProviderSlug,
        string? NyxUserToken,
        string? NyxRefreshToken,
        string? VerificationToken,
        string? ScopeId,
        string? WebhookBaseUrl,
        string? CredentialRef);

    private sealed record TestReplyRequest(string? ChatId, string? Message);

    private sealed record UpdateTokenRequest(string? NyxUserToken, string? NyxRefreshToken);
}
