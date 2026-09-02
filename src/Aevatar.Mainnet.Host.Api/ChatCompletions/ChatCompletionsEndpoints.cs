using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Mainnet.Host.Api.Responses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.ChatCompletions;

internal static class ChatCompletionsApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChatCompletionsApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/v1/chat/completions", HandleCreateChatCompletionAsync).AllowAnonymous();
        return app;
    }

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host handler owns caller resolution, route resolution, session registration, tool planning, direct provider execution, status updates, and protocol rendering in one request stack.
    //   New principle: Host maps HTTP/OpenAI frames only; typed Application facade owns Normalize -> Resolve Target -> Build Context -> Build Envelope -> Dispatch -> Receipt/Observe via the same LlmSessionGAgent run path as Responses/Messages.
    internal static async Task<IResult> HandleCreateChatCompletionAsync(
        HttpContext http,
        ChatCompletionsCreateRequest request,
        [FromServices] IChatCompletionsCommandFacade commandFacade,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandFacade);

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", "Authorization bearer token is required.");

        var commandRequest = ChatCompletionsProtocolMapper.ToCommandRequest(request);
        if (!commandRequest.Succeeded)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                commandRequest.ErrorCode ?? "invalid_request_error",
                commandRequest.ErrorMessage ?? "Invalid request.");
        }

        var callerScopeContext = ResponsesApiEndpoints.BuildCallerScopeResolutionContext(http, bearerToken);
        var result = await commandFacade.CreateAsync(commandRequest.Request!, callerScopeContext, ct);
        if (result.Error is not null)
            return ToErrorResult(result.Error.StatusCode, result.Error.Code, result.Error.Message);

        if (result.StreamPlan is not null)
        {
            await WriteStreamingChatCompletionAsync(
                http.Response,
                commandFacade,
                result.StreamPlan,
                ct);
            return Results.Empty;
        }

        if (result.Completed is not null)
        {
            return Results.Json(
                BuildCompletedChatCompletion(
                    result.Completed.Normalized,
                    result.Completed.CreatedAt,
                    result.Completed.Completion),
                JsonOptions,
                statusCode: StatusCodes.Status200OK);
        }

        if (result.Accepted is not null)
        {
            return Results.Json(
                BuildAcceptedChatCompletion(
                    result.Accepted.Normalized,
                    result.Accepted.CreatedAt),
                JsonOptions,
                statusCode: StatusCodes.Status200OK);
        }

        throw new InvalidOperationException("Chat Completions command facade returned no result.");
    }

    private static async Task WriteStreamingChatCompletionAsync(
        HttpResponse response,
        IChatCompletionsCommandFacade commandFacade,
        ChatCompletionsCreateCommandPlan plan,
        CancellationToken ct)
    {
        var normalized = plan.Normalized;
        var createdAt = plan.CreatedAt.ToUnixTimeSeconds();
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(ct);

        var completion = await commandFacade.StreamAsync(
            plan,
            async (delta, token) =>
            {
                if (!string.IsNullOrEmpty(delta.TextDelta))
                {
                    await WriteDataFrameAsync(
                        response,
                        BuildStreamingContentChunk(normalized, createdAt, delta.TextDelta),
                        token);
                }
            },
            ct);

        if (completion.Error is not null)
        {
            await WriteDataFrameAsync(
                response,
                BuildStreamingError(completion.Error.Code, completion.Error.Message),
                CancellationToken.None);
            await WriteDoneFrameAsync(response, CancellationToken.None);
            return;
        }

        if (completion.Completion is not null)
        {
            var sessionCompletion = completion.Completion;
            if (sessionCompletion.ToolCalls.Count > 0)
            {
                await WriteDataFrameAsync(
                    response,
                    BuildStreamingToolCallsSnapshotChunk(normalized, createdAt, sessionCompletion.ToolCalls),
                    CancellationToken.None);
            }

            await WriteDataFrameAsync(
                response,
                BuildStreamingStopChunk(normalized, createdAt, ResolveFinishReason(sessionCompletion)),
                CancellationToken.None);
            if (normalized.IncludeUsageInStream)
            {
                await WriteDataFrameAsync(
                    response,
                    BuildStreamingUsageChunk(normalized, createdAt, sessionCompletion.Usage),
                    CancellationToken.None);
            }

            await WriteDoneFrameAsync(response, CancellationToken.None);
            return;
        }

        throw new InvalidOperationException("Chat Completions stream facade returned no result.");
    }

    private static object BuildAcceptedChatCompletion(
        NormalizedChatCompletionsCommand normalized,
        long createdAt) =>
        new
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
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                    },
                    finish_reason = (string?)null,
                },
            },
            usage = (object?)null,
        };

    private static object BuildCompletedChatCompletion(
        NormalizedChatCompletionsCommand normalized,
        long createdAt,
        LlmSessionCompletionSnapshot completion) =>
        new
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
                    message = new
                    {
                        role = "assistant",
                        content = completion.OutputText,
                        tool_calls = completion.ToolCalls.Count == 0 ? null : completion.ToolCalls.Select(MapToolCall).ToArray(),
                    },
                    finish_reason = ResolveFinishReason(completion),
                },
            },
            usage = MapUsage(completion.Usage),
        };

    private static object BuildStreamingContentChunk(
        NormalizedChatCompletionsCommand normalized,
        long createdAt,
        string deltaText) =>
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
                        content = deltaText,
                    },
                    finish_reason = (string?)null,
                },
            },
        };

    private static object BuildStreamingToolCallsSnapshotChunk(
        NormalizedChatCompletionsCommand normalized,
        long createdAt,
        IReadOnlyList<LlmSessionCompletedToolCallSnapshot> toolCalls) =>
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
                        tool_calls = toolCalls.Select(MapStreamingToolCall).ToArray(),
                    },
                    finish_reason = (string?)null,
                },
            },
        };

    private static object MapStreamingToolCall(
        LlmSessionCompletedToolCallSnapshot toolCall,
        int index) =>
        new
        {
            index,
            id = toolCall.CallId,
            type = "function",
            function = new
            {
                name = toolCall.ToolName,
                arguments = toolCall.ResultJson ?? "{}",
            },
        };

    private static object BuildStreamingStopChunk(
        NormalizedChatCompletionsCommand normalized,
        long createdAt,
        string? finishReason) =>
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

    private static object MapToolCall(LlmSessionCompletedToolCallSnapshot toolCall) =>
        new
        {
            id = toolCall.CallId,
            type = "function",
            function = new
            {
                name = toolCall.ToolName,
                arguments = toolCall.ResultJson ?? "{}",
            },
        };

    private static string ResolveFinishReason(LlmSessionCompletionSnapshot completion) =>
        completion.ToolCalls.Count > 0 ? "tool_calls" : "stop";

    private static object BuildStreamingUsageChunk(
        NormalizedChatCompletionsCommand normalized,
        long createdAt,
        TokenUsage? usage) =>
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

    private static object? MapUsage(TokenUsage? usage) =>
        usage is null
            ? null
            : new
            {
                prompt_tokens = usage.PromptTokens,
                completion_tokens = usage.CompletionTokens,
                total_tokens = usage.TotalTokens,
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
