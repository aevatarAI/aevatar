using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Mainnet.Host.Api.Responses;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.ChatCompletions;

[SuppressMessage(
    "Maintainability",
    "CA1506:Avoid excessive class coupling",
    Justification = "Minimal API adapter composes caller scope, route resolution, session registration, and protocol shaping for one external facade.")]
internal static class ChatCompletionsApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChatCompletionsApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/v1/chat/completions", HandleCreateChatCompletionAsync).AllowAnonymous();
        return app;
    }

    [SuppressMessage(
        "Maintainability",
        "CA1506:Avoid excessive class coupling",
        Justification = "Minimal API adapter for one external compatibility endpoint; mirrors MessagesApiEndpoints.")]
    internal static async Task<IResult> HandleCreateChatCompletionAsync(
        HttpContext http,
        ChatCompletionsCreateRequest request,
        [FromServices] ILLMProviderFactory providerFactory,
        [FromServices] IResponsesCallerScopeResolver callerScopeResolver,
        [FromServices] IChatRoutePolicyQueryPort chatRoutePolicyQueryPort,
        [FromServices] ChatRouteResolver chatRouteResolver,
        [FromServices] IResponsesRouteResolver routeResolver,
        [FromServices] ILlmSessionRegistrationPort sessionRegistrationPort,
        [FromServices] IResponsesCompletionApplicationService completionService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(callerScopeResolver);
        ArgumentNullException.ThrowIfNull(chatRoutePolicyQueryPort);
        ArgumentNullException.ThrowIfNull(chatRouteResolver);
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(sessionRegistrationPort);
        ArgumentNullException.ThrowIfNull(completionService);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger("Aevatar.Mainnet.Host.Api.ChatCompletions");

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", "Authorization bearer token is required.");

        var normalizedResult = ChatCompletionsRequestNormalizer.Normalize(request);
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
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", ex.Message);
        }

        var routedModel = normalized.Model;
        var routeDecision = await ResponsesApiEndpoints.ResolveResponsesChatRouteAsync(
            chatRoutePolicyQueryPort,
            chatRouteResolver,
            callerScope,
            normalized.Model,
            ResponsesApiEndpoints.ResolveToolMode(normalized.DeclaredTools.Count, inlineToolResultCount: 0),
            ResponsesApiEndpoints.BuildContentHint(BuildRouteContentHint(normalized)),
            ct);
        ResponsesApiEndpoints.ApplyChatRouteDeprecationHeaders(http.Response, routeDecision);
        if (routeDecision.Action.Reject is not null)
        {
            return ToErrorResult(
                StatusCodes.Status403Forbidden,
                "chat_route_rejected",
                string.IsNullOrWhiteSpace(routeDecision.Action.Reject.Reason)
                    ? "The chat route policy rejected this request."
                    : routeDecision.Action.Reject.Reason);
        }

        var forwardToModel = routeDecision.Action.ForwardToModel;
        if (forwardToModel is not null)
        {
            if (HasToolDrivenRouting(forwardToModel))
            {
                return ToErrorResult(
                    StatusCodes.Status501NotImplemented,
                    "chat_route_action_not_supported",
                    "Tool-set and tool-choice chat route actions are not supported by /v1/chat/completions in v1.");
            }

            if (!string.IsNullOrWhiteSpace(forwardToModel.ModelName))
                routedModel = forwardToModel.ModelName.Trim();
        }
        else if (routeDecision.Action.ForwardToGagent is not null || routeDecision.Action.ForwardToTeam is not null)
        {
            return ToErrorResult(
                StatusCodes.Status501NotImplemented,
                "chat_route_action_not_supported",
                "ForwardToGAgent and ForwardToTeam are not supported by /v1/chat/completions in v1.");
        }

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
            logger.LogError(ex, "Failed to register llm session for chat completion {CompletionId}", normalized.CompletionId);
            return ToErrorResult(StatusCodes.Status500InternalServerError, "api_error", "Failed to register session.");
        }

        var toolProviderContext = ResponsesApiEndpoints.BuildToolProviderContext(
            callerScope,
            normalized.CompletionId,
            bearerToken);
        var toolClassification = await ResponsesToolClassifier.ClassifyAsync(
            normalized.DeclaredTools,
            Array.Empty<IResponsesToolProvider>(),
            toolProviderContext,
            logger,
            ct);

        var modelRoute = ResponsesModelRouteParser.Parse(routedModel);
        var effectiveModel = routedModel;
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
            [LLMRequestMetadataKeys.RequestId] = normalized.CompletionId,
            [ChannelMetadataKeys.RegistrationScopeId] = callerScope.ScopeId,
        };
        if (resolvedRouteValue is not null)
            llmMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = resolvedRouteValue;

        var llmRequest = new LLMRequest
        {
            Messages = [.. normalized.ChatMessages],
            RequestId = normalized.CompletionId,
            Metadata = llmMetadata,
            CallerContext = new LLMRequestCallerContext(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                normalized.CompletionId,
                new LLMRequestCallerCredentials(bearerToken)),
            Tools = toolClassification.EffectiveTools,
            Model = effectiveModel,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxTokens,
            ResponseFormat = normalized.ResponseFormat,
        };

        if (normalized.Stream)
        {
            await WriteStreamingChatCompletionAsync(
                http.Response,
                providerFactory,
                completionService,
                sessionRegistrationPort,
                logger,
                session,
                llmRequest,
                toolProviderContext.ToolContextMetadata,
                normalized,
                toolClassification,
                createdAt,
                ct);
            return Results.Empty;
        }

        try
        {
            var provider = providerFactory.GetDefault();
            var completion = await completionService.CollectAsync(
                provider,
                llmRequest,
                toolProviderContext.ToolContextMetadata,
                toolClassification,
                ct);
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Completed, ct);
            return Results.Json(
                BuildCompletedChatCompletion(normalized, completion, createdAt.ToUnixTimeSeconds()),
                JsonOptions,
                statusCode: StatusCodes.Status200OK);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            return ToErrorResult(ResolveUpstreamStatusCode(ex), ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Unexpected error processing /v1/chat/completions {CompletionId}", normalized.CompletionId);
            return ToErrorResult(StatusCodes.Status500InternalServerError, "api_error", "Internal server error.");
        }
    }

    private static string BuildRouteContentHint(NormalizedChatCompletionsRequest normalized) =>
        normalized.ChatMessages
            .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content
        ?? normalized.ChatMessages.LastOrDefault()?.Content
        ?? string.Empty;

    private static bool HasToolDrivenRouting(ForwardToModel forwardToModel) =>
        (forwardToModel.ToolSetRef is not null &&
         !string.IsNullOrWhiteSpace(forwardToModel.ToolSetRef.Name)) ||
        (forwardToModel.ToolChoiceHint is not null &&
         !string.IsNullOrWhiteSpace(forwardToModel.ToolChoiceHint.ToolName));

    private static async Task WriteStreamingChatCompletionAsync(
        HttpResponse response,
        ILLMProviderFactory providerFactory,
        IResponsesCompletionApplicationService completionService,
        ILlmSessionRegistrationPort sessionRegistrationPort,
        ILogger logger,
        LlmSessionRegistrationResult session,
        LLMRequest llmRequest,
        IReadOnlyDictionary<string, string> toolContextMetadata,
        NormalizedChatCompletionsRequest normalized,
        ResponsesToolClassification toolClassification,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(ct);

        try
        {
            var provider = providerFactory.GetDefault();
            var completion = await completionService.StreamAsync(
                provider,
                llmRequest,
                toolContextMetadata,
                toolClassification,
                async (delta, token) =>
                {
                    if (string.IsNullOrEmpty(delta))
                        return;

                    await WriteDataFrameAsync(
                        response,
                        BuildStreamingTextChunk(normalized, createdAt.ToUnixTimeSeconds(), delta),
                        token);
                },
                ct);

            foreach (var toolCall in completion.ForwardedToolCalls)
            {
                await WriteDataFrameAsync(
                    response,
                    BuildStreamingToolCallChunk(normalized, createdAt.ToUnixTimeSeconds(), toolCall),
                    ct);
            }

            await WriteDataFrameAsync(
                response,
                BuildStreamingStopChunk(
                    normalized,
                    createdAt.ToUnixTimeSeconds(),
                    completion.ForwardedToolCalls.Count > 0 ? "tool_calls" : "stop"),
                ct);

            if (normalized.IncludeUsageInStream && completion.Usage is not null)
            {
                await WriteDataFrameAsync(
                    response,
                    BuildStreamingUsageChunk(normalized, createdAt.ToUnixTimeSeconds(), completion.Usage),
                    ct);
            }

            await WriteDoneFrameAsync(response, ct);
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Completed, ct);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            await WriteDataFrameAsync(response, BuildStreamingError("authentication_required", ex.Message), CancellationToken.None);
            await WriteDoneFrameAsync(response, CancellationToken.None);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            await WriteDataFrameAsync(response, BuildStreamingError(ex.Kind.ToString().ToLowerInvariant(), ex.Message), CancellationToken.None);
            await WriteDoneFrameAsync(response, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Cancelled, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Streaming /v1/chat/completions {CompletionId} failed", normalized.CompletionId);
            await WriteDataFrameAsync(response, BuildStreamingError("api_error", "Internal server error."), CancellationToken.None);
            await WriteDoneFrameAsync(response, CancellationToken.None);
        }
    }

    private static object BuildCompletedChatCompletion(
        NormalizedChatCompletionsRequest normalized,
        ResponsesCompletionResult completion,
        long createdAt)
    {
        var message = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "assistant",
            ["content"] = completion.ForwardedToolCalls.Count > 0 && string.IsNullOrEmpty(completion.Text)
                ? null
                : completion.Text,
        };
        if (completion.ForwardedToolCalls.Count > 0)
            message["tool_calls"] = completion.ForwardedToolCalls.Select(MapToolCall).ToArray();

        return new
        {
            id = normalized.CompletionId,
            @object = "chat.completion",
            created = createdAt,
            model = normalized.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message,
                    finish_reason = completion.ForwardedToolCalls.Count > 0 ? "tool_calls" : "stop",
                },
            },
            usage = MapUsage(completion.Usage),
        };
    }

    private static object BuildStreamingTextChunk(
        NormalizedChatCompletionsRequest normalized,
        long createdAt,
        string delta) =>
        new
        {
            id = normalized.CompletionId,
            @object = "chat.completion.chunk",
            created = createdAt,
            model = normalized.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { content = delta },
                    finish_reason = (string?)null,
                },
            },
        };

    private static object BuildStreamingToolCallChunk(
        NormalizedChatCompletionsRequest normalized,
        long createdAt,
        ToolCall toolCall) =>
        new
        {
            id = normalized.CompletionId,
            @object = "chat.completion.chunk",
            created = createdAt,
            model = normalized.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new
                            {
                                index = 0,
                                id = toolCall.Id,
                                type = "function",
                                function = new
                                {
                                    name = toolCall.Name,
                                    arguments = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson)
                                        ? "{}"
                                        : toolCall.ArgumentsJson,
                                },
                            },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        };

    private static object BuildStreamingStopChunk(
        NormalizedChatCompletionsRequest normalized,
        long createdAt,
        string finishReason) =>
        new
        {
            id = normalized.CompletionId,
            @object = "chat.completion.chunk",
            created = createdAt,
            model = normalized.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = finishReason,
                },
            },
        };

    private static object BuildStreamingUsageChunk(
        NormalizedChatCompletionsRequest normalized,
        long createdAt,
        TokenUsage usage) =>
        new
        {
            id = normalized.CompletionId,
            @object = "chat.completion.chunk",
            created = createdAt,
            model = normalized.Model,
            choices = Array.Empty<object>(),
            usage = MapUsage(usage),
        };

    private static object BuildStreamingError(string code, string message) =>
        new
        {
            error = new
            {
                message,
                type = "server_error",
                code,
                param = (string?)null,
            },
        };

    private static object MapToolCall(ToolCall toolCall) =>
        new
        {
            id = toolCall.Id,
            type = "function",
            function = new
            {
                name = toolCall.Name,
                arguments = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson)
                    ? "{}"
                    : toolCall.ArgumentsJson,
            },
        };

    private static object? MapUsage(TokenUsage? usage) =>
        usage is null
            ? null
            : new
            {
                prompt_tokens = usage.PromptTokens,
                completion_tokens = usage.CompletionTokens,
                total_tokens = usage.TotalTokens,
            };

    private static LlmSessionRecord BuildSessionRecord(
        NormalizedChatCompletionsRequest normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt) =>
        new()
        {
            ResponseId = normalized.CompletionId,
            ScopeId = callerScope.ScopeId,
            OwnerSubject = callerScope.OwnerSubject,
            OriginKind = callerScope.OriginKind,
            PreviousResponseId = string.Empty,
            Status = LlmSessionStatus.Accepted,
            CreatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            UpdatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };

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

    private static int ResolveUpstreamStatusCode(NyxIdUpstreamException ex) =>
        ex.Status switch
        {
            400 => StatusCodes.Status400BadRequest,
            401 => StatusCodes.Status401Unauthorized,
            403 => StatusCodes.Status403Forbidden,
            404 => StatusCodes.Status404NotFound,
            429 => StatusCodes.Status429TooManyRequests,
            >= 500 => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status502BadGateway,
        };

    private static async Task WriteDataFrameAsync(
        HttpResponse response,
        object payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteDoneFrameAsync(HttpResponse response, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
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

    private static IResult ToErrorResult(int statusCode, string code, string message) =>
        Results.Json(new
        {
            error = new
            {
                message,
                type = GetErrorType(statusCode),
                code,
                param = (string?)null,
            },
        }, JsonOptions, statusCode: statusCode);

    private static string GetErrorType(int statusCode) =>
        statusCode switch
        {
            401 => "authentication_error",
            403 => "permission_error",
            429 => "rate_limit_error",
            >= 500 => "server_error",
            _ => "invalid_request_error",
        };
}
