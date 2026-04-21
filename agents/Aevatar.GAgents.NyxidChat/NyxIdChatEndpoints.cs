using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static class NyxIdChatEndpoints
{
    public static IEndpointRouteBuilder MapNyxIdChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("NyxIdChat");
        group.MapPost("/{scopeId}/nyxid-chat/conversations", HandleCreateConversationAsync);
        group.MapGet("/{scopeId}/nyxid-chat/conversations", HandleListConversationsAsync);
        group.MapPost("/{scopeId}/nyxid-chat/conversations/{actorId}:stream", HandleStreamMessageAsync);
        group.MapDelete("/{scopeId}/nyxid-chat/conversations/{actorId}", HandleDeleteConversationAsync);
        group.MapPost("/{scopeId}/nyxid-chat/conversations/{actorId}:approve", HandleApproveAsync);

        // NyxID Channel Bot Relay webhook — receives forwarded platform messages. NyxID drives
        // this callback; auth is carried via X-NyxID-User-Token rather than the JWT bearer we
        // validate in the fallback policy, so the route must stay anonymous. The diag + health
        // routes under the same prefix are operator probes that also must stay open.
        app.MapPost("/api/webhooks/nyxid-relay", HandleRelayWebhookAsync)
            .WithTags("NyxIdRelay")
            .AllowAnonymous();
        app.MapGet("/api/webhooks/nyxid-relay/health", () => Results.Json(new
        {
            status = "ok",
            endpoint = "/api/webhooks/nyxid-relay",
            last_check = DateTimeOffset.UtcNow,
        }))
            .WithTags("NyxIdRelay")
            .AllowAnonymous();

        // Temporary diagnostic: test NyxID gateway connectivity from this server
        app.MapPost("/api/webhooks/nyxid-relay/diag", async (HttpContext http, CancellationToken ct) =>
        {
            var token = http.Request.Headers["X-Test-Token"].FirstOrDefault()
                ?? http.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
            if (string.IsNullOrWhiteSpace(token))
                return Results.Json(new { error = "Provide token via X-Test-Token header" });

            var gateway = "https://nyx-api.chrono-ai.fun/api/v1/llm/gateway/v1/chat/completions";
            var body = """{"model":"gpt-5.4","messages":[{"role":"user","content":"hi"}],"max_tokens":10}""";

            using var client = new System.Net.Http.HttpClient();
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, gateway);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            req.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            // Clear default User-Agent to mimic clean request
            client.DefaultRequestHeaders.UserAgent.Clear();

            var resp = await client.SendAsync(req, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);

            return Results.Json(new
            {
                status = (int)resp.StatusCode,
                statusText = resp.StatusCode.ToString(),
                responseBody = respBody.Length > 500 ? respBody[..500] : respBody,
                serverOutboundIp = "check response headers",
            });
        })
            .WithTags("NyxIdRelay")
            .AllowAnonymous();

        // Access control for relay is handled by NyxID's route configuration.

        return app;
    }

    private static async Task<IResult> HandleCreateConversationAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IGAgentActorStore actorStore,
        CancellationToken ct)
    {
        // Conversation creation is fail-fast on IGAgentActorStore persistence.
        // NyxId chat depends on the registry being available; there is no
        // degraded mode where a conversation can run without being registered.
        var actorId = NyxIdChatServiceDefaults.GenerateActorId();
        await actorStore.AddActorAsync(scopeId, NyxIdChatServiceDefaults.GAgentTypeName, actorId, ct);
        return Results.Ok(new { actorId });
    }

    private static async Task<IResult> HandleListConversationsAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IGAgentActorStore actorStore,
        CancellationToken ct)
    {
        try
        {
            var groups = await actorStore.GetAsync(scopeId, ct);
            var actorIds = groups
                .FirstOrDefault(g => string.Equals(g.GAgentType, NyxIdChatServiceDefaults.GAgentTypeName, StringComparison.Ordinal))
                ?.ActorIds
                ?? [];
            return Results.Ok(actorIds.Select(actorId => new { actorId }));
        }
        catch (InvalidOperationException)
        {
            return Results.Ok(Array.Empty<object>());
        }
    }

    private static async Task HandleStreamMessageAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdChatStreamRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        var writer = new NyxIdChatSseWriter(http.Response);

        try
        {
            var accessToken = ExtractBearerToken(http);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var prompt = request.Prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt) && request.InputParts is not { Count: > 0 })
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Get or create the actor
            var actor = await actorRuntime.GetAsync(actorId)
                        ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);

            // Set up SSE response
            await writer.StartAsync(ct);

            var messageId = Guid.NewGuid().ToString("N");
            await writer.WriteRunStartedAsync(actorId, ct);

            // Subscribe to actor events and map to SSE frames
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = ct.Register(() => tcs.TrySetCanceled());

            await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                actor.Id,
                async envelope =>
                {
                    try
                    {
                        var terminalFrame = await MapAndWriteEventAsync(envelope, messageId, writer);
                        if (!string.IsNullOrWhiteSpace(terminalFrame))
                            tcs.TrySetResult(terminalFrame);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                },
                ct);

            // Build and dispatch ChatRequestEvent to the actor
            var chatRequest = new ChatRequestEvent
            {
                Prompt = prompt,
                SessionId = request.SessionId ?? messageId,
                ScopeId = scopeId,
            };
            if (request.InputParts is { Count: > 0 })
            {
                foreach (var part in request.InputParts)
                    chatRequest.InputParts.Add(part.ToProto());
            }
            chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = accessToken;
            chatRequest.Metadata["scope_id"] = scopeId;
            await InjectUserConfigMetadataAsync(http, chatRequest.Metadata, ct);
            await InjectUserMemoryAsync(http, chatRequest.Metadata, ct);
            await InjectConnectedServicesAsync(http, accessToken, chatRequest.Metadata, ct);

            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(chatRequest),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actor.Id },
                },
            };

            await actor.HandleEventAsync(envelope, ct);

            // Wait for completion or timeout
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(120_000, ct));

            if (completedTask == tcs.Task)
            {
                if (tcs.Task.IsFaulted)
                {
                    var ex = tcs.Task.Exception?.InnerException ?? tcs.Task.Exception;
                    await writer.WriteRunErrorAsync(ex?.Message ?? "An error occurred.", CancellationToken.None);
                }
                else if (string.Equals(tcs.Task.Result, "TEXT_MESSAGE_END", StringComparison.Ordinal))
                {
                    await writer.WriteRunFinishedAsync(CancellationToken.None);
                }
            }
            else
            {
                await writer.WriteRunErrorAsync("Request timed out.", CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID chat stream failed for actor {ActorId}", actorId);
            if (!writer.Started)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            await writer.WriteRunErrorAsync(ex.Message, CancellationToken.None);
        }
    }

    /// <summary>
    /// Maps AI event envelope payloads to NyxIdChat SSE frames.
    /// </summary>
    private static async ValueTask<string?> MapAndWriteEventAsync(
        EventEnvelope envelope,
        string messageId,
        NyxIdChatSseWriter writer)
    {
        var payload = envelope.Payload;
        if (payload is null)
            return null;

        if (payload.Is(TextMessageStartEvent.Descriptor))
        {
            await writer.WriteTextStartAsync(messageId, CancellationToken.None);
        }
        else if (payload.Is(TextMessageContentEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageContentEvent>();
            if (!string.IsNullOrEmpty(evt.Delta))
                await writer.WriteTextDeltaAsync(evt.Delta, CancellationToken.None);
        }
        else if (payload.Is(ToolCallEvent.Descriptor))
        {
            var evt = payload.Unpack<ToolCallEvent>();
            await writer.WriteToolCallStartAsync(evt.ToolName, evt.CallId, CancellationToken.None);
        }
        else if (payload.Is(ToolResultEvent.Descriptor))
        {
            var evt = payload.Unpack<ToolResultEvent>();
            await writer.WriteToolCallEndAsync(evt.CallId, evt.ResultJson, CancellationToken.None);
        }
        else if (payload.Is(ToolApprovalRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<ToolApprovalRequestEvent>();
            await writer.WriteToolApprovalRequestAsync(
                evt.RequestId, evt.ToolName, evt.ToolCallId,
                evt.ArgumentsJson, evt.IsDestructive, evt.TimeoutSeconds,
                CancellationToken.None);
        }
        else if (payload.Is(MediaContentEvent.Descriptor))
        {
            var evt = payload.Unpack<MediaContentEvent>();
            await writer.WriteMediaContentAsync(evt, CancellationToken.None);
        }
        else if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();

            // Check for LLM error markers
            if (!string.IsNullOrEmpty(evt.Content))
            {
                const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
                const string llmFailedPrefix = "LLM request failed:";
                if (evt.Content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
                {
                    await writer.WriteRunErrorAsync(
                        evt.Content[llmErrorPrefix.Length..].Trim(), CancellationToken.None);
                    return "RUN_ERROR";
                }

                if (evt.Content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
                {
                    await writer.WriteRunErrorAsync(evt.Content.Trim(), CancellationToken.None);
                    return "RUN_ERROR";
                }
            }

            await writer.WriteTextEndAsync(messageId, CancellationToken.None);
            return "TEXT_MESSAGE_END";
        }

        return null;
    }

    private static async Task<IResult> HandleDeleteConversationAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        [FromServices] IGAgentActorStore actorStore,
        [FromServices] IChatHistoryStore chatHistoryStore,
        CancellationToken ct)
    {
        await chatHistoryStore.DeleteConversationAsync(scopeId, actorId, ct);
        await actorStore.RemoveActorAsync(scopeId, NyxIdChatServiceDefaults.GAgentTypeName, actorId, ct);
        return Results.Ok();
    }

    /// <summary>
    /// Handles tool approval decisions from the frontend.
    /// Opens an SSE connection to stream the continuation chat response.
    /// </summary>
    private static async Task HandleApproveAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdApprovalRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        var writer = new NyxIdChatSseWriter(http.Response);

        try
        {
            var accessToken = ExtractBearerToken(http);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var actor = await actorRuntime.GetAsync(actorId);
            if (actor == null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await writer.StartAsync(ct);
            await writer.WriteRunStartedAsync(actorId, ct);

            var messageId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = ct.Register(() => tcs.TrySetCanceled());

            await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                actor.Id,
                async envelope =>
                {
                    try
                    {
                        var terminalFrame = await MapAndWriteEventAsync(envelope, messageId, writer);
                        if (!string.IsNullOrWhiteSpace(terminalFrame))
                            tcs.TrySetResult(terminalFrame);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                },
                ct);

            // Send the approval decision to the actor
            var decisionEvent = new ToolApprovalDecisionEvent
            {
                RequestId = request.RequestId,
                SessionId = request.SessionId ?? scopeId,
                Approved = request.Approved,
                Reason = request.Reason ?? string.Empty,
            };

            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(decisionEvent),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actor.Id },
                },
            };

            await actor.HandleEventAsync(envelope, ct);

            // Wait for the continuation chat to complete (or timeout)
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(120_000, ct));

            if (completedTask == tcs.Task)
            {
                if (tcs.Task.IsFaulted)
                {
                    var ex = tcs.Task.Exception?.InnerException ?? tcs.Task.Exception;
                    await writer.WriteRunErrorAsync(ex?.Message ?? "An error occurred.", CancellationToken.None);
                }
                else if (string.Equals(tcs.Task.Result, "TEXT_MESSAGE_END", StringComparison.Ordinal))
                {
                    await writer.WriteRunFinishedAsync(CancellationToken.None);
                }
            }
            else
            {
                await writer.WriteRunErrorAsync("Approval continuation timed out.", CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID approval stream failed for actor {ActorId}", actorId);
            if (!writer.Started)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            await writer.WriteRunErrorAsync(ex.Message, CancellationToken.None);
        }
    }

    public sealed record NyxIdApprovalRequest(
        string? RequestId,
        bool Approved = true,
        string? Reason = null,
        string? SessionId = null);

    private static async Task InjectUserConfigMetadataAsync(
        HttpContext http,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("Aevatar.NyxId.Chat.UserConfig");

        var preferencesStore = http.RequestServices.GetService<INyxIdUserLlmPreferencesStore>();
        if (preferencesStore == null)
        {
            logger?.LogWarning("INyxIdUserLlmPreferencesStore not registered — skipping user config injection");
            return;
        }

        try
        {
            var preferences = await preferencesStore.GetAsync(ct);
            logger?.LogInformation(
                "User config loaded: model={Model}, route={Route}, maxToolRounds={MaxToolRounds}",
                preferences.DefaultModel ?? "<empty>",
                preferences.PreferredRoute ?? "<empty>",
                preferences.MaxToolRounds);

            if (!string.IsNullOrWhiteSpace(preferences.DefaultModel))
                metadata[LLMRequestMetadataKeys.ModelOverride] = preferences.DefaultModel.Trim();
            if (!string.IsNullOrWhiteSpace(preferences.PreferredRoute))
                metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = preferences.PreferredRoute.Trim();
            if (preferences.MaxToolRounds > 0)
                metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride] = preferences.MaxToolRounds.ToString();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load user config from chrono-storage — falling back to server defaults");
        }
    }

    private static async Task InjectUserMemoryAsync(
        HttpContext http,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var memoryStore = http.RequestServices.GetService<IUserMemoryStore>();
        if (memoryStore == null)
            return;

        try
        {
            var section = await memoryStore.BuildPromptSectionAsync(2000, ct);
            if (!string.IsNullOrWhiteSpace(section))
                metadata[LLMRequestMetadataKeys.UserMemoryPrompt] = section;
        }
        catch
        {
            // Best-effort
        }
    }

    private static async Task InjectConnectedServicesAsync(
        HttpContext http,
        string accessToken,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var client = http.RequestServices.GetService<NyxIdApiClient>();
        if (client is null)
            return;

        try
        {
            var memCache = http.RequestServices.GetService<IMemoryCache>();
            var cacheKey = $"nyxid:services:{ComputeTokenHash(accessToken)}";

            string? servicesJson = null;
            if (memCache is not null)
                servicesJson = memCache.Get<string>(cacheKey);

            if (servicesJson is null)
            {
                servicesJson = await client.DiscoverProxyServicesAsync(accessToken, ct);
                memCache?.Set(cacheKey, servicesJson, TimeSpan.FromSeconds(60));
            }

            var specSource = http.RequestServices.GetService<IConnectedServiceSpecSource>();
            var context = await BuildConnectedServicesContextAsync(servicesJson, specSource, accessToken, ct);
            if (!string.IsNullOrWhiteSpace(context))
                metadata[LLMRequestMetadataKeys.ConnectedServicesContext] = context;
        }
        catch
        {
            // Best-effort — agent still works without capability context
        }
    }

    internal static async Task<string> BuildConnectedServicesContextAsync(
        string servicesJson,
        IConnectedServiceSpecSource? specSource,
        string accessToken,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<connected-services>");
        sb.AppendLine("Your capabilities based on connected services:");

        var hintRequests = new List<ServiceHintRequest>();

        try
        {
            using var doc = JsonDocument.Parse(servicesJson);
            var root = doc.RootElement;

            JsonElement items = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("services", out var svc))
                    items = svc;
                else if (root.TryGetProperty("data", out var data))
                    items = data;
            }

            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var serviceId = item.TryGetProperty("id", out var id) ? id.GetString()
                                  : item.TryGetProperty("service_id", out var sid) ? sid.GetString()
                                  : null;
                    var slug = item.TryGetProperty("slug", out var s) ? s.GetString() : null;
                    var name = item.TryGetProperty("name", out var n) ? n.GetString()
                             : item.TryGetProperty("label", out var l) ? l.GetString()
                             : slug;
                    var baseUrl = item.TryGetProperty("endpoint_url", out var e) ? e.GetString()
                                : item.TryGetProperty("base_url", out var b) ? b.GetString()
                                : null;
                    var openapiUrl = item.TryGetProperty("openapi_url", out var oa) ? oa.GetString() : null;

                    if (string.IsNullOrWhiteSpace(slug)) continue;
                    hintRequests.Add(new ServiceHintRequest(slug, serviceId, name, openapiUrl));

                    sb.Append($"- **{name ?? slug}** (slug: `{slug}`)");
                    if (!string.IsNullOrWhiteSpace(baseUrl))
                        sb.Append($" — base: {baseUrl}");
                    sb.AppendLine();
                }
            }
        }
        catch
        {
            // Parse failure — return what we have
        }

        if (hintRequests.Count == 0)
        {
            sb.AppendLine("No services connected yet. Use nyxid_catalog to browse and connect services.");
        }

        sb.AppendLine("Use nyxid_proxy with slug + path to call any service. Use code_execute for sandbox.");
        sb.AppendLine("</connected-services>");

        string hints;
        if (specSource is not null && !string.IsNullOrWhiteSpace(accessToken))
        {
            hints = await NyxIdServiceApiHints.BuildHintsSectionAsync(hintRequests, specSource, accessToken, ct);
        }
        else
        {
            hints = NyxIdServiceApiHints.BuildHintsSection(hintRequests.Select(r => r.Slug));
        }

        if (!string.IsNullOrEmpty(hints))
        {
            sb.AppendLine();
            sb.Append(hints);
        }

        return sb.ToString();
    }

    private static string ComputeTokenHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes)[..16]; // Short hash for cache key
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;

        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authHeader["Bearer ".Length..].Trim();

        return null;
    }

    /// <summary>
    /// Decode the JWT payload (without verification) to extract the 'sub' claim.
    /// Used by the relay endpoint to resolve the user's scope ID for chrono-storage
    /// config access, since the auth middleware has already run by the time the handler
    /// executes and won't re-process the injected Authorization header.
    /// </summary>
    private static string? TryExtractJwtSubject(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;

            // JWT payload is base64url-encoded
            var payload = parts[1];
            // Pad to multiple of 4
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sub", out var sub))
                return sub.GetString()?.Trim();
            return null;
        }
        catch
        {
            return null;
        }
    }

    public sealed record NyxIdChatStreamRequest(
        string? Prompt,
        string? SessionId = null,
        IReadOnlyList<ContentPartDto>? InputParts = null);

    public sealed record ContentPartDto(
        string Type,
        string? Text = null,
        string? DataBase64 = null,
        string? MediaType = null,
        string? Uri = null,
        string? Name = null)
    {
        public ChatContentPart ToProto() => new()
        {
            Kind = Type?.ToLowerInvariant() switch
            {
                "image" => ChatContentPartKind.Image,
                "audio" => ChatContentPartKind.Audio,
                "video" => ChatContentPartKind.Video,
                "text" => ChatContentPartKind.Text,
                _ => ChatContentPartKind.Unspecified,
            },
            Text = Text ?? string.Empty,
            DataBase64 = DataBase64 ?? string.Empty,
            MediaType = MediaType ?? string.Empty,
            Uri = Uri ?? string.Empty,
            Name = Name ?? string.Empty,
        };
    }

    // ─── NyxID Channel Bot Relay ───

    /// <summary>
    /// Receives forwarded platform messages from NyxID Channel Bot Relay.
    /// Verifies HMAC signature, dispatches to NyxIdChat actor, collects response, returns sync reply.
    /// </summary>
    private static async Task<IResult> HandleRelayWebhookAsync(
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] IGAgentActorStore actorStore,
        [FromServices] NyxIdRelayOptions relayOptions,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Relay");

        try
        {
            // ─── Parse payload ───
            RelayMessage? message;
            try
            {
                message = await http.Request.ReadFromJsonAsync<RelayMessage>(RelayJsonOptions, ct);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse relay payload");
                return FriendlyReply("I received a message but couldn't understand it. Please try again.");
            }

            if (message is null || string.IsNullOrWhiteSpace(message.Content?.Text))
                return FriendlyReply("I received an empty message. Please send some text.");

            // ─── Auth ───
            var userToken = http.Request.Headers["X-NyxID-User-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(userToken))
            {
                logger.LogWarning("Relay callback missing X-NyxID-User-Token header");
                return FriendlyReply("Authentication is not configured properly. " +
                    "Please ask the bot owner to check the channel relay setup.");
            }

            // Resolve user scope for chrono-storage config access
            var jwtScopeId = TryExtractJwtSubject(userToken);
            var scopeId = jwtScopeId ?? message.Agent?.ApiKeyId ?? "default";

            if (!string.IsNullOrWhiteSpace(jwtScopeId))
            {
                var claims = new[] { new System.Security.Claims.Claim("sub", jwtScopeId) };
                var identity = new System.Security.Claims.ClaimsIdentity(claims, "NyxIdRelay");
                http.User = new System.Security.Claims.ClaimsPrincipal(identity);
            }

            // Note: config.json in chrono-storage cannot be read in relay flow because
            // ChronoStorageCatalogBlobClient reads the Bearer token from Authorization header,
            // which is not present on relay callbacks (token is in X-NyxID-User-Token instead).
            // InjectUserConfigMetadataAsync will silently fall back to server defaults.

            // ─── Resolve conversation ───
            var platform = message.Platform ?? "unknown";
            var conversationPlatformId = message.Conversation?.PlatformId ?? "unknown";
            var conversationId = message.Conversation?.Id;
            if (string.IsNullOrWhiteSpace(conversationId))
                conversationId = $"{platform}-{conversationPlatformId}";

            var actorId = $"nyxid-relay-{conversationId}";

            logger.LogInformation(
                "Relay message: platform={Platform}, conversation={ConversationId}, sender={Sender}",
                platform, conversationId, message.Sender?.DisplayName);

            // ─── Get or create actor ───
            // Relay follows the same strict lifecycle contract as create:
            // registry persistence via IGAgentActorStore is mandatory, and
            // registry failures must surface instead of silently degrading.
            var actor = await actorRuntime.GetAsync(actorId)
                        ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);
            await actorStore.AddActorAsync(scopeId, NyxIdChatServiceDefaults.GAgentTypeName, actorId, ct);

            // ─── Subscribe and collect response ───
            var responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var responseBuilder = new StringBuilder();
            string? errorMessage = null;
            using var ctr = ct.Register(() => responseTcs.TrySetCanceled());

            await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                actor.Id,
                envelope =>
                {
                    var payload = envelope.Payload;
                    if (payload is null) return Task.CompletedTask;

                    if (payload.Is(TextMessageContentEvent.Descriptor))
                    {
                        var evt = payload.Unpack<TextMessageContentEvent>();
                        if (!string.IsNullOrEmpty(evt.Delta))
                            responseBuilder.Append(evt.Delta);
                    }
                    else if (payload.Is(TextMessageEndEvent.Descriptor))
                    {
                        var evt = payload.Unpack<TextMessageEndEvent>();
                        if (!string.IsNullOrEmpty(evt.Content))
                        {
                            const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
                            const string llmFailedPrefix = "LLM request failed:";
                            if (evt.Content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
                                errorMessage = evt.Content[llmErrorPrefix.Length..].Trim();
                            else if (evt.Content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
                                errorMessage = evt.Content.Trim();
                        }
                        responseTcs.TrySetResult(responseBuilder.ToString());
                    }

                    return Task.CompletedTask;
                },
                ct);

            // ─── Dispatch to actor ───
            // SessionId = per-message unique ID (for idempotent retry),
            // NOT conversationId (which is per-chat and would collide across messages).
            var relayMessageId = message.MessageId;
            var sessionId = !string.IsNullOrWhiteSpace(relayMessageId)
                ? $"{conversationId}-{relayMessageId}"
                : $"{conversationId}-{Guid.NewGuid():N}";

            var chatRequest = new ChatRequestEvent
            {
                Prompt = message.Content.Text,
                SessionId = sessionId,
                ScopeId = scopeId,
            };
            chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = userToken;
            chatRequest.Metadata["scope_id"] = scopeId;
            chatRequest.Metadata["relay.platform"] = message.Platform ?? "";
            chatRequest.Metadata["relay.sender"] = message.Sender?.DisplayName ?? "";
            chatRequest.Metadata["relay.message_id"] = message.MessageId ?? "";
            await InjectUserConfigMetadataAsync(http, chatRequest.Metadata, ct);
            await InjectUserMemoryAsync(http, chatRequest.Metadata, ct);

            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(chatRequest),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actor.Id },
                },
            };

            await actor.HandleEventAsync(envelope, ct);

            // ─── Wait for response ───
            var timeoutMs = relayOptions.ResponseTimeoutSeconds * 1000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var completed = await Task.WhenAny(responseTcs.Task, Task.Delay(timeoutMs, ct));
            sw.Stop();

            string replyText;
            if (completed == responseTcs.Task && responseTcs.Task.IsCompletedSuccessfully)
            {
                replyText = responseTcs.Task.Result;
                logger.LogInformation("Relay response in {ElapsedMs}ms, length={Length}",
                    sw.ElapsedMilliseconds, replyText.Length);
            }
            else
            {
                var partial = responseBuilder.ToString();
                logger.LogWarning("Relay timed out after {ElapsedMs}ms, partial={Length}",
                    sw.ElapsedMilliseconds, partial.Length);
                replyText = partial.Length > 0
                    ? partial
                    : "Sorry, it's taking too long to respond. Please try again.";
            }

            // ─── Translate errors to friendly messages ───
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                logger.LogWarning("Relay LLM error: conversation={ConversationId}, error={Error}",
                    conversationId, errorMessage);

                replyText = ClassifyError(errorMessage);

                if (relayOptions.EnableDebugDiagnostics)
                {
                    var config = http.RequestServices.GetService<IConfiguration>();
                    var diagnostic = BuildRelayDiagnostic(chatRequest.Metadata, config, errorMessage);
                    replyText += $"\n\n[Debug]\n{diagnostic}";
                }
            }
            else if (string.IsNullOrWhiteSpace(replyText))
            {
                logger.LogWarning("Relay empty response: conversation={ConversationId}", conversationId);
                replyText = "Sorry, I wasn't able to generate a response. Please try again.";
            }

            return FriendlyReply(replyText);
        }
        catch (OperationCanceledException)
        {
            return FriendlyReply("The request was cancelled. Please try again.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Relay handler unexpected error");
            return FriendlyReply("Sorry, an unexpected error occurred. Please try again later.");
        }
    }

    /// <summary>Classify a technical LLM error into a user-friendly message.</summary>
    private static string ClassifyError(string error)
    {
        if (error.Contains("403", StringComparison.Ordinal) ||
            error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            return "Sorry, I can't reach the AI service right now (403 Forbidden).";

        if (error.Contains("401", StringComparison.Ordinal) ||
            error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return "Sorry, authentication with the AI service failed (401).";

        if (error.Contains("429", StringComparison.Ordinal) ||
            error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("too many", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the AI service is busy right now (429). Please wait a moment and try again.";

        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the AI service took too long to respond. Please try again.";

        if (error.Contains("model", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the configured AI model is not available.";

        return "Sorry, something went wrong while generating a response.";
    }

    /// <summary>
    /// Build diagnostic block for relay error replies. Only included when
    /// <see cref="NyxIdRelayOptions.EnableDebugDiagnostics"/> is true.
    /// </summary>
    private static string BuildRelayDiagnostic(
        Google.Protobuf.Collections.MapField<string, string> metadata,
        IConfiguration? configuration,
        string errorMessage)
    {
        var modelOverride = metadata.TryGetValue(LLMRequestMetadataKeys.ModelOverride, out var m) ? m : null;
        var serverDefault = configuration?["Aevatar:NyxId:DefaultModel"] ?? "(OpenAIModel option)";
        var route = metadata.TryGetValue(LLMRequestMetadataKeys.NyxIdRoutePreference, out var r)
            && !string.IsNullOrWhiteSpace(r) ? r : "gateway";
        var hasToken = metadata.ContainsKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        var scope = metadata.TryGetValue("scope_id", out var s) ? s : "<unknown>";

        var model = !string.IsNullOrWhiteSpace(modelOverride)
            ? $"{modelOverride} (from config.json)"
            : $"server-default={serverDefault}";

        var error = errorMessage.Length > 300 ? errorMessage[..300] + "..." : errorMessage;

        return $"Model: {model}\nRoute: {route}\nScope: {scope}\nToken: {(hasToken ? "present" : "MISSING")}\nError: {error}";
    }

    private static IResult FriendlyReply(string text) =>
        Results.Json(new { reply = new { text } });

    // ─── Relay payload models ───

    private static readonly JsonSerializerOptions RelayJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class RelayMessage
    {
        public string? MessageId { get; set; }
        public string? Platform { get; set; }
        public RelayAgent? Agent { get; set; }
        public RelayConversation? Conversation { get; set; }
        public RelaySender? Sender { get; set; }
        public RelayContent? Content { get; set; }
        public string? Timestamp { get; set; }
    }

    private sealed class RelayAgent
    {
        public string? ApiKeyId { get; set; }
        public string? Name { get; set; }
    }

    private sealed class RelayConversation
    {
        public string? Id { get; set; }
        public string? PlatformId { get; set; }
        public string? Type { get; set; }
    }

    private sealed class RelaySender
    {
        public string? PlatformId { get; set; }
        public string? DisplayName { get; set; }
    }

    private sealed class RelayContent
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
    }

}
