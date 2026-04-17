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
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] IEnumerable<IPlatformAdapter> adapters,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Callback");
        var diagnostics = http.RequestServices.GetService<IChannelRuntimeDiagnostics>();

        // Resolve registration from projection read model
        var registration = await queryPort.GetAsync(registrationId, ct);
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

        if (ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var resumeCommand))
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
            RegistrationToken = registration.NyxUserToken,
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
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
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
            EncryptKey = request.EncryptKey?.Trim() ?? string.Empty,
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

        // Wait for projection to materialize the registration document.
        // Without this, webhooks arriving immediately after registration
        // (e.g. Lark URL verification) see 404 because the read model
        // has not caught up. Mirrors the pattern in HandleUpdateTokenAsync.
        var materialized = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(250, ct);
            if (await queryPort.GetAsync(registrationId, ct) is not null)
            {
                materialized = true;
                break;
            }
        }

        if (!materialized)
        {
            logger.LogError(
                "Registration {RegistrationId} dispatched but not materialized within 5s — projection pipeline may be unhealthy",
                registrationId);
            return Results.Json(new
            {
                status = "error",
                error = "Registration dispatched but projection did not materialize within timeout. Check projection pipeline health.",
                registration_id = registrationId,
            }, statusCode: 500);
        }

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

    private static async Task<IResult> HandleUpdateTokenAsync(
        string registrationId,
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IChannelBotRegistrationQueryPort queryPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.Registration");

        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return Results.NotFound(new { error = "Registration not found" });

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

        // Snapshot projection version. Orphaned documents retain a stale version
        // that never advances — this lets us detect the actor dropping the command.
        var versionBefore = await queryPort.GetStateVersionAsync(registrationId, ct) ?? -1;

        // Always dispatch to the actor — it is the authority on current state.
        var actor = await GetOrCreateRegistrationActorAsync(actorRuntime);
        var cmd = new ChannelBotUpdateTokenCommand
        {
            RegistrationId = registrationId,
            NyxUserToken = newToken,
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

        // Poll: require BOTH version advance AND desired token visible.
        var confirmed = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500, ct);
            var versionAfter = await queryPort.GetStateVersionAsync(registrationId, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;
            var after = await queryPort.GetAsync(registrationId, ct);
            if (after is not null && string.Equals(after.NyxUserToken, newToken, StringComparison.Ordinal))
            {
                confirmed = true;
                break;
            }
        }

        if (!confirmed)
        {
            logger.LogWarning("Token update for {RegistrationId} dispatched but not confirmed by projection", registrationId);
            return Results.Json(new
            {
                status = "error",
                error = "Token update dispatched but not confirmed. The registration may not exist in the actor's state.",
                registration_id = registrationId,
            }, statusCode: 500);
        }

        return Results.Ok(new { status = "token_updated", registration_id = registrationId });
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
            var delivery = await adapter.SendReplyAsync(message, inbound, registration, nyxClient, cts.Token);
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
        string? VerificationToken,
        string? ScopeId,
        string? WebhookBaseUrl,
        string? EncryptKey);

    private sealed record TestReplyRequest(string? ChatId, string? Message);

    private sealed record UpdateTokenRequest(string? NyxUserToken);
}
