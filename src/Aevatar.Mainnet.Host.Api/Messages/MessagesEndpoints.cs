using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Mainnet.Host.Api.Responses;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Messages;

internal static class MessagesApiEndpoints
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

    [SuppressMessage(
        "Maintainability",
        "CA1506:Avoid excessive class coupling",
        Justification = "Minimal API adapter for one external endpoint; mirrors ResponsesApiEndpoints.")]
    internal static async Task<IResult> HandleCreateMessageAsync(
        HttpContext http,
        MessagesCreateRequest request,
        [FromServices] ILLMProviderFactory providerFactory,
        [FromServices] IResponsesCallerScopeResolver callerScopeResolver,
        [FromServices] IResponsesRouteResolver routeResolver,
        [FromServices] ILlmSessionRegistrationPort sessionRegistrationPort,
        [FromServices] IResponsesCompletionApplicationService completionService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(callerScopeResolver);
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(sessionRegistrationPort);
        ArgumentNullException.ThrowIfNull(completionService);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(request);
        var logger = loggerFactory.CreateLogger("Aevatar.Mainnet.Host.Api.Messages");

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_error", "Authorization bearer token is required.");

        var normalizedResult = MessagesRequestNormalizer.Normalize(request);
        if (!normalizedResult.Succeeded)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                normalizedResult.ErrorCode ?? "invalid_request_error",
                normalizedResult.ErrorMessage ?? "Invalid request.");
        }

        var normalized = normalizedResult.Request!;
        ResponsesCallerScope callerScope;
        try
        {
            callerScope = await callerScopeResolver.ResolveAsync(bearerToken, http, ct);
        }
        catch (ResponsesCallerScopeUnavailableException ex)
        {
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_error", ex.Message);
        }

        // Path B is stateless: register a new LlmSession per request, no
        // previous_response_id continuation. The session id mirrors the
        // Anthropic message id so projection/audit can correlate.
        var createdAt = DateTimeOffset.UtcNow;
        LlmSessionRegistrationResult session;
        try
        {
            session = await sessionRegistrationPort.RegisterAsync(
                BuildSessionRecord(normalized, callerScope, createdAt),
                ct);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to register llm session for message {MessageId}", normalized.MessageId);
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "api_error",
                "Failed to register session.");
        }

        // Tools come from the inbound declaration only. Substitute/additive
        // tool providers (TodoWrite, Task, WebFetch) are intentionally NOT
        // injected here because Anthropic Messages clients (Claude Code in
        // particular) ship their own tool harness on top of the response;
        // injecting Aevatar's substitutes would shadow client tools.
        var toolClassification = ResponsesToolClassifier.Classify(
            normalized.DeclaredTools,
            Array.Empty<IResponsesToolProvider>(),
            logger);

        // OpenRouter-style vendor prefix routing (same as Path A). If the
        // model is `vendor/name`, resolve the route value through the catalog;
        // unknown slugs fall through to gateway default.
        var modelRoute = ResponsesModelRouteParser.Parse(normalized.Model);
        var effectiveModel = normalized.Model;
        string? resolvedRouteValue = null;
        if (modelRoute.RouteSlug is not null)
        {
            resolvedRouteValue = await routeResolver
                .ResolveRouteValueAsync(modelRoute.RouteSlug, bearerToken, ct)
                .ConfigureAwait(false);
            if (resolvedRouteValue is not null)
                effectiveModel = modelRoute.Model;
        }

        var llmMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = normalized.MessageId,
            [ChannelMetadataKeys.RegistrationScopeId] = callerScope.ScopeId,
        };
        if (resolvedRouteValue is not null)
            llmMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = resolvedRouteValue;

        var toolContextMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = normalized.MessageId,
            [LLMRequestMetadataKeys.ResponseId] = normalized.MessageId,
            [LLMRequestMetadataKeys.ScopeId] = callerScope.ScopeId,
            [LLMRequestMetadataKeys.OwnerSubject] = callerScope.OwnerSubject,
            [ChannelMetadataKeys.RegistrationScopeId] = callerScope.ScopeId,
            [LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken,
        };

        var llmRequest = new LLMRequest
        {
            Messages = [.. normalized.ChatMessages],
            RequestId = normalized.MessageId,
            Metadata = llmMetadata,
            CallerContext = new LLMRequestCallerContext(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                normalized.MessageId,
                new LLMRequestCallerCredentials(bearerToken)),
            Tools = toolClassification.EffectiveTools,
            Model = effectiveModel,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxTokens,
        };

        if (normalized.DroppedImageContent)
        {
            logger.LogWarning(
                "Image content blocks dropped from Messages request {MessageId}; Path B is text-only in v1.",
                normalized.MessageId);
        }

        if (normalized.Stream)
        {
            await WriteStreamingMessageAsync(
                http.Response,
                providerFactory,
                completionService,
                sessionRegistrationPort,
                logger,
                session,
                llmRequest,
                toolContextMetadata,
                normalized,
                toolClassification,
                ct);
            return Results.Empty;
        }

        try
        {
            var provider = providerFactory.GetDefault();
            var completion = await completionService.CollectAsync(
                provider,
                llmRequest,
                toolContextMetadata,
                toolClassification,
                ct);
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Completed, ct);
            return Results.Json(
                BuildCompletedMessage(normalized, completion),
                JsonOptions,
                statusCode: StatusCodes.Status200OK);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_error", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            var statusCode = ex.Status switch
            {
                400 => StatusCodes.Status400BadRequest,
                401 => StatusCodes.Status401Unauthorized,
                403 => StatusCodes.Status403Forbidden,
                404 => StatusCodes.Status404NotFound,
                429 => StatusCodes.Status429TooManyRequests,
                >= 500 => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status502BadGateway,
            };
            return ToErrorResult(statusCode, ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Unexpected error processing /v1/messages {MessageId}", normalized.MessageId);
            return ToErrorResult(StatusCodes.Status500InternalServerError, "api_error", "Internal server error.");
        }
    }

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
