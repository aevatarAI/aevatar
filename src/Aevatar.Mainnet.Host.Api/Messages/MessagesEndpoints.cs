using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.Responses;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Messages;

internal static partial class MessagesApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMessagesApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Path B (Anthropic Messages) is a stateless facade over the same
        // LlmSessionGAgent / NyxIdLLMProvider typed run pipeline as /v1/responses.
        // AllowAnonymous matches the Responses
        // endpoint — NyxID issues opaque api keys, not JWTs, so the JwtBearer
        // fallback policy would 401 valid callers.
        app.MapPost("/v1/messages", HandleCreateMessageAsync).AllowAnonymous();
        return app;
    }

    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Mainnet Minimal API handlers (ResponsesEndpoints / MessagesEndpoints) inject long lists of application/runtime collaborators and perform caller resolution / route / session / LLM orchestration inline.
    //   New principle: Host handlers parse/authenticate HTTP only + delegate to typed Application command/query facade that owns Normalize -> Resolve Target -> Build Context -> Dispatch/Observe lifecycle. SSE rendering stays at the boundary.
    // Refactor (iter81/cluster-081-direct-response-completion-not-session-fact):
    //   Old pattern: direct Responses/Messages held terminal completion in request-local result; LlmSession only marked Completed
    //   New principle: record typed LlmSessionCompletion on session for direct paths; terminal protocol output renders from session contract/readmodel
    internal static async Task<IResult> HandleCreateMessageAsync(
        HttpContext http,
        MessagesCreateRequest request,
        [FromServices] IMessagesCommandFacade commandFacade,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandFacade);

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_error", "Authorization bearer token is required.");

        var commandRequest = MessagesProtocolMapper.ToCommandRequest(request);
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
            await WriteStreamingMessageAsync(
                http.Response,
                commandFacade,
                result.StreamPlan,
                ct);
            return Results.Empty;
        }

        if (result.Accepted is not null)
        {
            return Results.Json(
                BuildAcceptedMessage(result.Accepted.Normalized),
                JsonOptions,
                statusCode: StatusCodes.Status200OK);
        }

        if (result.Completed is not null)
        {
            return Results.Json(
                BuildCompletedMessage(result.Completed.Normalized, result.Completed.Completion),
                JsonOptions,
                statusCode: StatusCodes.Status200OK);
        }

        throw new InvalidOperationException("Messages command facade returned no result.");
    }

    private static async Task WriteStreamingMessageAsync(
        HttpResponse response,
        IMessagesCommandFacade commandFacade,
        MessagesCreateCommandPlan plan,
        CancellationToken ct)
    {
        var normalized = plan.Normalized;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(ct);

        var textStarted = false;
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

        var completion = await commandFacade.StreamAsync(
            plan,
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

        if (completion.Error is not null)
        {
            await WriteSseFrameAsync(response, "error", new
            {
                type = "error",
                error = new { type = completion.Error.Code, message = completion.Error.Message },
            }, CancellationToken.None);
            return;
        }

        if (completion.Accepted is not null)
        {
            await WriteSseFrameAsync(response, "message_delta", new
            {
                type = "message_delta",
                delta = new
                {
                    stop_reason = (string?)null,
                    stop_sequence = (string?)null,
                },
                usage = new
                {
                    output_tokens = 0,
                },
            }, CancellationToken.None);

            await WriteSseFrameAsync(response, "message_stop", new
            {
                type = "message_stop",
            }, CancellationToken.None);
            return;
        }

        if (textStarted)
        {
            await WriteSseFrameAsync(response, "content_block_stop", new
            {
                type = "content_block_stop",
                index = 0,
            }, ct);
        }

        var sessionCompletion = completion.Completion!;
        var toolCalls = ToToolCalls(sessionCompletion.ToolCalls);
        var nextBlockIndex = textStarted ? 1 : 0;
        foreach (var toolCall in toolCalls)
        {
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

        var stopReason = toolCalls.Count > 0 ? "tool_use" : "end_turn";
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
                output_tokens = sessionCompletion.Usage?.CompletionTokens ?? 0,
            },
        }, ct);

        await WriteSseFrameAsync(response, "message_stop", new
        {
            type = "message_stop",
        }, ct);
    }

    private static object BuildAcceptedMessage(NormalizedMessagesRequest normalized) =>
        new
        {
            id = normalized.MessageId,
            type = "message",
            role = "assistant",
            model = normalized.Model,
            content = Array.Empty<object>(),
            stop_reason = (string?)null,
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = 0,
                output_tokens = 0,
            },
        };

    private static object BuildCompletedMessage(
        NormalizedMessagesRequest normalized,
        LlmSessionCompletionSnapshot completion)
    {
        var contentBlocks = new List<object>();
        if (!string.IsNullOrEmpty(completion.OutputText))
        {
            contentBlocks.Add(new { type = "text", text = completion.OutputText });
        }
        var toolCalls = ToToolCalls(completion.ToolCalls);
        foreach (var toolCall in toolCalls)
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

        var stopReason = toolCalls.Count > 0 ? "tool_use" : "end_turn";
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

    private static IReadOnlyList<ToolCall> ToToolCalls(
        IReadOnlyList<LlmSessionCompletedToolCallSnapshot> toolCalls) =>
        toolCalls
            .Select(static toolCall => new ToolCall
            {
                Id = toolCall.CallId,
                Name = toolCall.ToolName,
                ArgumentsJson = toolCall.ResultJson ?? "{}",
            })
            .ToArray();

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
