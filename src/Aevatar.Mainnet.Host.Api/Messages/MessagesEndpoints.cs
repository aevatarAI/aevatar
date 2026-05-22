using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Mainnet.Host.Api.Responses;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Messages;

internal static partial class MessagesApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMessagesApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Path B (Anthropic Messages) is a stateless facade over the same
        // LlmSessionGAgent / NyxIdLLMProvider / IResponsesCompletionApplicationService
        // pipeline as /v1/responses. AllowAnonymous matches the Responses
        // endpoint — NyxID issues opaque api keys, not JWTs, so the JwtBearer
        // fallback policy would 401 valid callers.
        app.MapPost("/v1/messages", HandleCreateMessageAsync).AllowAnonymous();
        return app;
    }

    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Mainnet Minimal API handlers (ResponsesEndpoints / MessagesEndpoints) inject long lists of application/runtime collaborators and perform caller resolution / route / session / LLM orchestration inline.
    //   New principle: Host handlers parse/authenticate HTTP only + delegate to typed Application command/query facade that owns Normalize -> Resolve Target -> Build Context -> Dispatch/Observe lifecycle. SSE rendering stays at the boundary.
    internal static async Task<IResult> HandleCreateMessageAsync(
        HttpContext http,
        MessagesCreateRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_error", "Authorization bearer token is required.");

        var facade = ActivatorUtilities.CreateInstance<MessagesCommandFacade>(http.RequestServices);
        return await facade.CreateAsync(http, request, bearerToken, ct);
    }

    private static string BuildRouteContentHint(NormalizedMessagesRequest normalized) =>
        normalized.ChatMessages
            .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content
        ?? normalized.ChatMessages.LastOrDefault()?.Content
        ?? string.Empty;

    private static async Task WriteStreamingMessageAsync(
        HttpResponse response,
        ILLMProviderFactory providerFactory,
        IResponsesCompletionApplicationService completionService,
        ILlmSessionRegistrationPort sessionRegistrationPort,
        ILogger logger,
        LlmSessionRegistrationResult session,
        LLMRequest llmRequest,
        IReadOnlyDictionary<string, string> toolContextMetadata,
        NormalizedMessagesRequest normalized,
        ResponsesToolClassification toolClassification,
        CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(ct);

        var textStarted = false;
        TokenUsage? usage = null;

        try
        {
            var provider = providerFactory.GetDefault();
            await WriteSseFrameAsync(response, "message_start", new
            {
                type = "message_start",
                message = new
                {
                    id = normalized.MessageId,
                    type = "message",
                    role = "assistant",
                    model = normalized.Model,
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    stop_sequence = (string?)null,
                    usage = new { input_tokens = 0, output_tokens = 0 },
                },
            }, ct);

            var completion = await completionService.StreamAsync(
                provider,
                llmRequest,
                toolContextMetadata,
                toolClassification,
                async (delta, token) =>
                {
                    if (string.IsNullOrEmpty(delta))
                        return;
                    if (!textStarted)
                    {
                        textStarted = true;
                        await WriteSseFrameAsync(response, "content_block_start", new
                        {
                            type = "content_block_start",
                            index = 0,
                            content_block = new { type = "text", text = string.Empty },
                        }, token);
                    }
                    await WriteSseFrameAsync(response, "content_block_delta", new
                    {
                        type = "content_block_delta",
                        index = 0,
                        delta = new { type = "text_delta", text = delta },
                    }, token);
                },
                ct);
            usage = completion.Usage;

            if (textStarted)
            {
                await WriteSseFrameAsync(response, "content_block_stop", new
                {
                    type = "content_block_stop",
                    index = 0,
                }, ct);
            }

            var nextBlockIndex = textStarted ? 1 : 0;
            foreach (var toolCall in completion.ForwardedToolCalls)
            {
                using var argsDoc = SafeParseJson(toolCall.ArgumentsJson);
                await WriteSseFrameAsync(response, "content_block_start", new
                {
                    type = "content_block_start",
                    index = nextBlockIndex,
                    content_block = new
                    {
                        type = "tool_use",
                        id = toolCall.Id,
                        name = toolCall.Name,
                        input = new { },
                    },
                }, ct);
                await WriteSseFrameAsync(response, "content_block_delta", new
                {
                    type = "content_block_delta",
                    index = nextBlockIndex,
                    delta = new
                    {
                        type = "input_json_delta",
                        partial_json = toolCall.ArgumentsJson ?? "{}",
                    },
                }, ct);
                await WriteSseFrameAsync(response, "content_block_stop", new
                {
                    type = "content_block_stop",
                    index = nextBlockIndex,
                }, ct);
                nextBlockIndex++;
            }

            var stopReason = completion.ForwardedToolCalls.Count > 0 ? "tool_use" : "end_turn";
            await WriteSseFrameAsync(response, "message_delta", new
            {
                type = "message_delta",
                delta = new
                {
                    stop_reason = stopReason,
                    stop_sequence = (string?)null,
                },
                usage = new
                {
                    output_tokens = usage?.CompletionTokens ?? 0,
                },
            }, ct);

            await WriteSseFrameAsync(response, "message_stop", new
            {
                type = "message_stop",
            }, ct);

            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Completed, ct);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            await WriteSseFrameAsync(response, "error", new
            {
                type = "error",
                error = new { type = "authentication_error", message = ex.Message },
            }, CancellationToken.None);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            await WriteSseFrameAsync(response, "error", new
            {
                type = "error",
                error = new { type = ex.Kind.ToString().ToLowerInvariant(), message = ex.Message },
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Cancelled, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Streaming /v1/messages {MessageId} failed", normalized.MessageId);
            await WriteSseFrameAsync(response, "error", new
            {
                type = "error",
                error = new { type = "api_error", message = "Internal server error." },
            }, CancellationToken.None);
        }
    }

    private static object BuildCompletedMessage(
        NormalizedMessagesRequest normalized,
        ResponsesCompletionResult completion)
    {
        var contentBlocks = new List<object>();
        if (!string.IsNullOrEmpty(completion.Text))
        {
            contentBlocks.Add(new { type = "text", text = completion.Text });
        }
        foreach (var toolCall in completion.ForwardedToolCalls)
        {
            using var argsDoc = SafeParseJson(toolCall.ArgumentsJson);
            contentBlocks.Add(new
            {
                type = "tool_use",
                id = toolCall.Id,
                name = toolCall.Name,
                input = argsDoc.RootElement.Clone(),
            });
        }

        var stopReason = completion.ForwardedToolCalls.Count > 0 ? "tool_use" : "end_turn";
        return new
        {
            id = normalized.MessageId,
            type = "message",
            role = "assistant",
            model = normalized.Model,
            content = contentBlocks,
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = completion.Usage?.PromptTokens ?? 0,
                output_tokens = completion.Usage?.CompletionTokens ?? 0,
            },
        };
    }

    private static LlmSessionRecord BuildSessionRecord(
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt)
    {
        return new LlmSessionRecord
        {
            ResponseId = normalized.MessageId,
            ScopeId = callerScope.ScopeId,
            OwnerSubject = callerScope.OwnerSubject,
            OriginKind = callerScope.OriginKind,
            PreviousResponseId = string.Empty,
            Status = LlmSessionStatus.Accepted,
            CreatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            UpdatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };
    }

    private static async Task TryUpdateSessionStatusAsync(
        ILlmSessionRegistrationPort port,
        ILogger logger,
        LlmSessionRegistrationResult session,
        LlmSessionStatus status,
        CancellationToken ct)
    {
        try
        {
            await port.UpdateStatusAsync(session.ActorId, session.ResponseId, status, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update llm session {ResponseId} to {Status}", session.ResponseId, status);
        }
    }

    private static async Task WriteSseFrameAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    private static JsonDocument SafeParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return JsonDocument.Parse("{}");
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue("Authorization", out var auth))
            return null;
        var raw = auth.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        const string prefix = "Bearer ";
        return raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? raw[prefix.Length..].Trim()
            : null;
    }

    private static IResult ToErrorResult(int statusCode, string errorType, string message)
    {
        return Results.Json(new
        {
            type = "error",
            error = new { type = errorType, message },
        }, JsonOptions, statusCode: statusCode);
    }


}
