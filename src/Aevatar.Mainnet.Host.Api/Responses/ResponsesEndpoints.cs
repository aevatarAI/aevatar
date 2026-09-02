using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Responses;

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
// Refactor (iter81/cluster-081-direct-response-completion-not-session-fact):
//   Old pattern: direct Responses/Messages held terminal completion in request-local result; LlmSession only marked Completed
//   New principle: record typed LlmSessionCompletion on session for direct paths; terminal protocol output renders from session contract/readmodel
internal static partial class ResponsesApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string NyxIdIdentityTokenHeader = "X-NyxID-Identity-Token";
    internal const string NyxIdDelegationTokenHeader = "X-NyxID-Delegation-Token";

    public static IEndpointRouteBuilder MapResponsesApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1").WithTags("Responses");
        // Auth is endpoint-internal: each handler extracts inbound caller
        // evidence for the shared caller-scope resolver. Opt out of the host's
        // FallbackPolicy.RequireAuthenticatedUser() so opaque NyxID API keys
        // (non-JWT) reach the handler instead of being 401'd by JwtBearer.
        group.MapPost("/responses", HandleCreateResponseAsync).AllowAnonymous();
        group.MapPost("/responses/{id}/cancel", HandleCancelResponseAsync).AllowAnonymous();
        group.MapGet("/models", HandleListModelsAsync).AllowAnonymous();
        return app;
    }

    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Mainnet Minimal API handlers (ResponsesEndpoints / MessagesEndpoints) inject long lists of application/runtime collaborators and perform caller resolution / route / session / LLM orchestration inline.
    //   New principle: Host handlers parse/authenticate HTTP only + delegate to typed Application command/query facade that owns Normalize -> Resolve Target -> Build Context -> Dispatch/Observe lifecycle. SSE rendering stays at the boundary.
    internal static async Task<IResult> HandleCreateResponseAsync(
        HttpContext http,
        ResponsesCreateRequest request,
        [FromServices] IResponsesCommandFacade commandFacade,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandFacade);

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authorization bearer token is required.");

        var commandRequest = ResponsesProtocolMapper.ToCommandRequest(request);
        if (!commandRequest.Succeeded)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                commandRequest.ErrorCode ?? "invalid_request_error",
                commandRequest.ErrorMessage ?? "Invalid request.");
        }

        var callerScopeContext = BuildCallerScopeResolutionContext(http, bearerToken);
        var result = await commandFacade.CreateAsync(commandRequest.Request!, callerScopeContext, ct);
        if (result.Error is not null)
            return ToErrorResult(result.Error.StatusCode, result.Error.Code, result.Error.Message);

        if (result.StreamPlan is not null)
        {
            await WriteStreamResponseAsync(
                http.Response,
                commandFacade,
                result.StreamPlan,
                ct);
            return Results.Empty;
        }

        if (result.Completed is not null)
        {
            return Results.Json(
                BuildCompletedResponse(
                    result.Completed.Normalized,
                    result.Completed.CreatedAt,
                    result.Completed.Completion.CompletedAt?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    result.Completed.Completion.OutputText,
                    ToToolCalls(result.Completed.Completion.ToolCalls),
                    result.Completed.Completion.Usage is null ? null : MapUsage(result.Completed.Completion.Usage)),
                statusCode: StatusCodes.Status200OK);
        }

        throw new InvalidOperationException("Responses command facade returned no result.");
    }

    internal static async Task<IResult> HandleCancelResponseAsync(
        HttpContext http,
        [FromRoute] string id,
        [FromServices] IResponsesCommandFacade commandFacade,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(commandFacade);

        var responseId = id?.Trim();
        if (string.IsNullOrWhiteSpace(responseId))
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "response_id_required",
                "response id is required.");
        }

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authorization bearer token is required.");

        var callerScopeContext = BuildCallerScopeResolutionContext(http, bearerToken);
        var result = await commandFacade.CancelAsync(responseId, callerScopeContext, ct);
        if (result.Error is not null)
            return ToErrorResult(result.Error.StatusCode, result.Error.Code, result.Error.Message);

        return Results.Json(new
        {
            id = result.ResponseId,
            @object = "response",
            status = "cancelled",
            cancelled_at = result.CancelledAt,
        }, JsonOptions, statusCode: StatusCodes.Status200OK);
    }

    private static async Task WriteStreamResponseAsync(
        HttpResponse response,
        IResponsesCommandFacade commandFacade,
        ResponsesCreateCommandPlan plan,
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

        var sequenceNumber = 0;
        var createdResponse = BuildCreatedResponse(normalized, createdAt);
        await WriteSseFrameAsync(
            response,
            "response.created",
            new
            {
                type = "response.created",
                response = createdResponse,
                sequence_number = ++sequenceNumber,
            },
            ct);

        var outputItem = BuildOutputMessage(normalized.MessageItemId, "in_progress", text: null);
        await WriteSseFrameAsync(
            response,
            "response.output_item.added",
            new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = outputItem,
                sequence_number = ++sequenceNumber,
            },
            ct);

        var completion = await commandFacade.StreamAsync(
            plan,
            async (delta, token) =>
            {
                await WriteSseFrameAsync(
                    response,
                    "response.output_text.delta",
                    new
                    {
                        type = "response.output_text.delta",
                        item_id = normalized.MessageItemId,
                        output_index = 0,
                        content_index = 0,
                        delta,
                        sequence_number = ++sequenceNumber,
                    },
                    token);
            },
            ct);

        if (completion.Error is not null)
        {
            await WriteStreamFailureAsync(
                response,
                normalized,
                createdAt,
                ++sequenceNumber,
                completion.Error.Code,
                completion.Error.Message,
                CancellationToken.None);
            return;
        }

        var sessionCompletion = completion.Completion!;
        var completedText = sessionCompletion.OutputText;
        await WriteSseFrameAsync(
            response,
            "response.output_text.done",
            new
            {
                type = "response.output_text.done",
                item_id = normalized.MessageItemId,
                output_index = 0,
                content_index = 0,
                text = completedText,
                sequence_number = ++sequenceNumber,
            },
            ct);

        var completedOutputItem = BuildOutputMessage(normalized.MessageItemId, "completed", completedText);
        await WriteSseFrameAsync(
            response,
            "response.output_item.done",
            new
            {
                type = "response.output_item.done",
                output_index = 0,
                item = completedOutputItem,
                sequence_number = ++sequenceNumber,
            },
            ct);

        var toolCalls = ToToolCalls(sessionCompletion.ToolCalls);
        var nextOutputIndex = 1;
        foreach (var toolCall in toolCalls)
        {
            var functionCallItem = BuildFunctionCallOutputItem(toolCall);
            await WriteSseFrameAsync(
                response,
                "response.output_item.added",
                new
                {
                    type = "response.output_item.added",
                    output_index = nextOutputIndex,
                    item = functionCallItem,
                    sequence_number = ++sequenceNumber,
                },
                ct);
            await WriteSseFrameAsync(
                response,
                "response.output_item.done",
                new
                {
                    type = "response.output_item.done",
                    output_index = nextOutputIndex,
                    item = functionCallItem,
                    sequence_number = ++sequenceNumber,
                },
                ct);
            nextOutputIndex++;
        }

        var completedResponse = BuildCompletedResponse(
            normalized,
            createdAt,
            sessionCompletion.CompletedAt?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            completedText,
            toolCalls,
            sessionCompletion.Usage is null ? null : MapUsage(sessionCompletion.Usage));

        await WriteSseFrameAsync(
            response,
            "response.completed",
            new
            {
                type = "response.completed",
                response = completedResponse,
                sequence_number = ++sequenceNumber,
            },
            ct);
    }

    private static ResponsesResponseSnapshot BuildCreatedResponse(
        NormalizedResponsesRequest normalized,
        long createdAt)
    {
        return new ResponsesResponseSnapshot
        {
            Id = normalized.ResponseId,
            CreatedAt = createdAt,
            Status = "in_progress",
            Input = [BuildInputMessage(normalized.Prompt)],
            MaxOutputTokens = normalized.MaxOutputTokens,
            Model = normalized.Model,
            Output = [],
            PreviousResponseId = normalized.PreviousResponseId,
            ParallelToolCalls = true,
            Reasoning = new ResponsesReasoningSettings(),
            Store = false,
            Temperature = normalized.Temperature,
            ToolChoice = "auto",
            Tools = [],
            Truncation = "disabled",
            Usage = null,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static ResponsesResponseSnapshot BuildCompletedResponse(
        NormalizedResponsesRequest normalized,
        long createdAt,
        long completedAt,
        string outputText,
        IReadOnlyList<ToolCall> toolCalls,
        ResponsesUsage? usage)
    {
        var output = new List<object>
        {
            BuildOutputMessage(normalized.MessageItemId, "completed", outputText),
        };
        output.AddRange(toolCalls.Select(BuildFunctionCallOutputItem));

        return new ResponsesResponseSnapshot
        {
            Id = normalized.ResponseId,
            CreatedAt = createdAt,
            Status = "completed",
            CompletedAt = completedAt,
            Input = [BuildInputMessage(normalized.Prompt)],
            MaxOutputTokens = normalized.MaxOutputTokens,
            Model = normalized.Model,
            Output = output,
            PreviousResponseId = normalized.PreviousResponseId,
            ParallelToolCalls = true,
            Reasoning = new ResponsesReasoningSettings(),
            Store = false,
            Temperature = normalized.Temperature,
            ToolChoice = "auto",
            Tools = [],
            Truncation = "disabled",
            Usage = usage,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static ResponsesInputMessage BuildInputMessage(string prompt)
    {
        return new ResponsesInputMessage
        {
            Content =
            [
                new ResponsesInputTextContent
                {
                    Text = prompt,
                },
            ],
        };
    }

    private static ResponsesUsage MapUsage(TokenUsage usage) =>
        new()
        {
            InputTokens = usage.PromptTokens,
            InputTokensDetails = new ResponsesInputTokensDetails(),
            OutputTokens = usage.CompletionTokens,
            TotalTokens = usage.TotalTokens,
            OutputTokensDetails = new ResponsesOutputTokensDetails(),
        };

    private static IReadOnlyList<ToolCall> ToToolCalls(
        IReadOnlyList<LlmSessionCompletedToolCallSnapshot> toolCalls) =>
        toolCalls.Select(ToToolCall).ToArray();

    private static ToolCall ToToolCall(LlmSessionCompletedToolCallSnapshot toolCall) =>
        new()
        {
            Id = toolCall.CallId,
            Name = toolCall.ToolName,
            ArgumentsJson = toolCall.ResultJson ?? "{}",
        };

    private static ResponsesOutputMessage BuildOutputMessage(string id, string status, string? text)
    {
        IReadOnlyList<ResponsesOutputTextContent> content = text is null
            ? []
            :
            [
                new ResponsesOutputTextContent
                {
                    Text = text,
                },
            ];

        return new ResponsesOutputMessage
        {
            Id = id,
            Status = status,
            Content = string.IsNullOrWhiteSpace(text)
                ? []
                : content,
        };
    }

    private static ResponsesFunctionCallOutputItem BuildFunctionCallOutputItem(ToolCall toolCall) =>
        new()
        {
            Id = "fc_" + SanitizeOutputId(toolCall.Id),
            Status = "completed",
            CallId = toolCall.Id,
            Name = toolCall.Name,
            Arguments = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson,
        };

    private static string SanitizeOutputId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ResponsesIds.NewOpaqueId();

        var builder = new StringBuilder(id.Length);
        foreach (var ch in id)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return builder.ToString();
    }

    private static async Task WriteSseFrameAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\n");
        await response.Body.WriteAsync(bytes, ct);
        bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteStreamFailureAsync(
        HttpResponse response,
        NormalizedResponsesRequest normalized,
        long createdAt,
        int sequenceNumber,
        string code,
        string message,
        CancellationToken ct)
    {
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var failedResponse = BuildFailedResponse(normalized, createdAt, completedAt, code, message);
        await WriteSseFrameAsync(
            response,
            "response.failed",
            new
            {
                type = "response.failed",
                response = failedResponse,
                sequence_number = sequenceNumber,
            },
            ct);

        await WriteSseFrameAsync(
            response,
            "error",
            new
            {
                type = "error",
                code,
                message,
                param = (string?)null,
                sequence_number = sequenceNumber + 1,
            },
            ct);
    }

    private static ResponsesResponseSnapshot BuildFailedResponse(
        NormalizedResponsesRequest normalized,
        long createdAt,
        long completedAt,
        string code,
        string message)
    {
        return new ResponsesResponseSnapshot
        {
            Id = normalized.ResponseId,
            CreatedAt = createdAt,
            Status = "failed",
            CompletedAt = completedAt,
            Error = new ResponsesResponseError
            {
                Code = code,
                Message = message,
            },
            Input = [BuildInputMessage(normalized.Prompt)],
            MaxOutputTokens = normalized.MaxOutputTokens,
            Model = normalized.Model,
            Output = [],
            PreviousResponseId = normalized.PreviousResponseId,
            ParallelToolCalls = true,
            Reasoning = new ResponsesReasoningSettings(),
            Store = false,
            Temperature = normalized.Temperature,
            ToolChoice = "auto",
            Tools = [],
            Truncation = "disabled",
            Usage = null,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    internal static ResponsesToolProviderContext BuildToolProviderContext(
        ResponsesCallerScope callerScope,
        string responseId,
        string bearerToken)
    {
        ArgumentNullException.ThrowIfNull(callerScope);

        return new ResponsesToolProviderContext(
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity(responseId, null),
                Credentials = new AgentToolCredentials(bearerToken, null, null),
                Caller = new AgentToolCallerContext(callerScope.ScopeId, callerScope.OwnerSubject, responseId, callerScope.OwnerSubject),
                Channel = new AgentToolChannelContext(
                    callerScope.OriginKind.ToString(),
                    null,
                    callerScope.ScopeId,
                    null,
                    null),
            });
    }

    internal static async Task<ChatRouteDecision> ResolveResponsesChatRouteAsync(
        IChatRoutePolicyQueryPort queryPort,
        ChatRouteResolver resolver,
        ResponsesChatRouteDecisionRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queryPort);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(request);

        var ownerScope = OwnerScope.ForNyxIdNative(request.CallerScope.ScopeId);
        var snapshot = await queryPort.LookupForCallerAsync(ownerScope, ct);
        return resolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.NyxResponses,
            CallerScope = new OwnerScope
            {
                NyxUserId = ownerScope.NyxUserId,
                Platform = ownerScope.Platform,
                RegistrationScopeId = ownerScope.RegistrationScopeId,
                SenderId = ownerScope.SenderId,
            },
            Channel = string.Empty,
            CommandName = request.CommandName,
            ContentHint = request.ContentHint,
            ToolMode = request.ToolMode,
            Model = request.Model,
        });
    }

    internal static ToolMode ResolveToolMode(int declaredToolCount, int inlineToolResultCount)
    {
        if (inlineToolResultCount > 0)
            return ToolMode.Inline;
        return declaredToolCount > 0 ? ToolMode.Declared : ToolMode.None;
    }

    internal static string BuildContentHint(string? content)
    {
        var normalized = content?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;
        const int maxContentHintLength = 160;
        return normalized.Length <= maxContentHintLength
            ? normalized
            : normalized[..maxContentHintLength];
    }

    private static IResult ToErrorResult(int statusCode, string code, string message) =>
        Results.Json(
            new ResponsesApiErrorResponse
            {
                Error = new ResponsesApiError
                {
                    Code = code,
                    Message = message,
                    Type = GetErrorType(statusCode),
                    Param = null,
                },
            },
            statusCode: statusCode);

    private static string GetErrorType(int statusCode) =>
        statusCode switch
        {
            401 => "authentication_error",
            403 => "permission_error",
            429 => "rate_limit_error",
            >= 500 => "server_error",
            _ => "invalid_request_error",
        };

    private static string? ExtractBearerToken(HttpContext http)
    {
        var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;

        return authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : null;
    }

    internal static ResponsesCallerScopeResolutionContext BuildCallerScopeResolutionContext(
        HttpContext http,
        string bearerToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        return new ResponsesCallerScopeResolutionContext(
            bearerToken,
            ExtractHeaderValue(http, NyxIdIdentityTokenHeader),
            ExtractHeaderValue(http, NyxIdDelegationTokenHeader));
    }

    private static string? ExtractHeaderValue(HttpContext http, string headerName)
    {
        if (!http.Request.Headers.TryGetValue(headerName, out var values))
            return null;

        var value = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>OpenAI-spec `GET /v1/models`. Fans out across every NyxID-routed service the caller
    /// can reach (gateway providers + proxy-plane LLM services) and returns the union, with
    /// `vendor/model`-prefixed ids for non-gateway routes so the create handler can recover the
    /// route via <see cref="ResponsesModelRouteParser"/>. Gateway models stay bare for back-compat
    /// with existing callers that send plain `gpt-5.4` / `claude-3-5-sonnet-...`.</summary>
    internal static async Task<IResult> HandleListModelsAsync(
        HttpContext http,
        [FromServices] IResponsesModelsAggregator aggregator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(aggregator);

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return ToErrorResult(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authorization bearer token is required.");
        }

        var entries = await aggregator.AggregateAsync(bearerToken, ct).ConfigureAwait(false);
        return Results.Json(new ResponsesModelsListResponse { Data = entries });
    }
}
