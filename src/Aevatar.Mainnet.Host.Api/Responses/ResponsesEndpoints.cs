using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal static class ResponsesApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapResponsesApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1").WithTags("Responses");
        group.MapPost("/responses", HandleCreateResponseAsync);
        group.MapPost("/responses/{id}/cancel", HandleCancelResponseAsync);
        return app;
    }

    [SuppressMessage(
        "Maintainability",
        "CA1506:Avoid excessive class coupling",
        Justification = "This Minimal API adapter coordinates one external Responses endpoint across HTTP, " +
                        "caller scope, durable session registration, and SSE shaping.")]
    internal static async Task<IResult> HandleCreateResponseAsync(
        HttpContext http,
        ResponsesCreateRequest request,
        [FromServices] ILLMProviderFactory providerFactory,
        [FromServices] IResponsesCallerScopeResolver callerScopeResolver,
        [FromServices] IResponseSessionRegistrationPort responseSessionRegistrationPort,
        [FromServices] IResponseSessionQueryPort responseSessionQueryPort,
        [FromServices] IEnumerable<IResponsesToolProvider> toolProviders,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(callerScopeResolver);
        ArgumentNullException.ThrowIfNull(responseSessionRegistrationPort);
        ArgumentNullException.ThrowIfNull(responseSessionQueryPort);
        ArgumentNullException.ThrowIfNull(toolProviders);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(request);
        var logger = loggerFactory.CreateLogger("Aevatar.Mainnet.Host.Api.Responses");

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authorization bearer token is required.");

        var normalizedResult = ResponsesRequestNormalizer.Normalize(request);
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

        ResponseSessionSnapshot? previousSnapshot = null;
        if (normalized.PreviousResponseId is not null)
        {
            previousSnapshot = await responseSessionQueryPort.GetByResponseIdAsync(normalized.PreviousResponseId, ct);
            var previousError = ValidatePreviousResponse(previousSnapshot, callerScope);
            if (previousError is not null)
                return previousError;
        }

        if (normalized.ToolResults.Count > 0 && previousSnapshot is null)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "previous_response_required",
                "function_call_output requires previous_response_id.");
        }

        if (previousSnapshot is not null &&
            TryBuildAlreadyResolvedToolResultResponse(normalized, previousSnapshot, out var alreadyResolvedResult))
        {
            return alreadyResolvedResult;
        }

        if (previousSnapshot is not null)
        {
            var toolResultError = await PersistIncomingToolResultsAsync(
                responseSessionRegistrationPort,
                previousSnapshot,
                normalized,
                ct);
            if (toolResultError is not null)
                return toolResultError;
        }

        var createdAt = DateTimeOffset.UtcNow;
        ResponseSessionRegistrationResult responseSession;
        try
        {
            responseSession = await responseSessionRegistrationPort.RegisterAsync(
                BuildResponseSessionRecord(normalized, callerScope, createdAt),
                ct);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "session_registration_failed",
                ex.Message);
        }

        var toolClassification = ResponsesToolClassifier.Classify(
            normalized.DeclaredTools,
            toolProviders,
            logger);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken,
            [LLMRequestMetadataKeys.RequestId] = normalized.ResponseId,
            [LLMRequestMetadataKeys.ResponseId] = normalized.ResponseId,
            [LLMRequestMetadataKeys.ScopeId] = callerScope.ScopeId,
            [LLMRequestMetadataKeys.OwnerSubject] = callerScope.OwnerSubject,
            [ChannelMetadataKeys.RegistrationScopeId] = callerScope.ScopeId,
        };

        var llmRequest = new LLMRequest
        {
            Messages = BuildLlmMessages(normalized, previousSnapshot),
            RequestId = normalized.ResponseId,
            Metadata = metadata,
            Tools = toolClassification.EffectiveTools,
            Model = normalized.Model,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxOutputTokens,
        };

        if (normalized.Stream)
        {
            await WriteStreamResponseAsync(
                http.Response,
                providerFactory,
                responseSessionRegistrationPort,
                logger,
                responseSession,
                llmRequest,
                normalized,
                previousSnapshot,
                toolClassification,
                createdAt,
                ct);
            return Results.Empty;
        }

        try
        {
            var provider = providerFactory.GetDefault();
            var completion = await CollectToolAwareCompletionAsync(
                provider,
                llmRequest,
                toolClassification,
                ct);
            var forwardedToolCalls = completion.ForwardedToolCalls;
            await PersistForwardedToolCallsAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                toolClassification,
                forwardedToolCalls,
                DateTimeOffset.UtcNow,
                ct);
            await TryResolveIncomingToolResultsAsync(
                responseSessionRegistrationPort,
                logger,
                previousSnapshot,
                normalized,
                CancellationToken.None);
            var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Completed,
                CancellationToken.None);
            var completed = BuildCompletedResponse(
                normalized,
                createdAt.ToUnixTimeSeconds(),
                completedAt,
                completion.Text,
                forwardedToolCalls,
                completion.Usage);
            return Results.Json(completed, statusCode: StatusCodes.Status200OK);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Failed,
                CancellationToken.None);
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Failed,
                CancellationToken.None);
            var statusCode = ex.Status switch
            {
                401 or 403 => StatusCodes.Status401Unauthorized,
                429 => StatusCodes.Status429TooManyRequests,
                503 => StatusCodes.Status503ServiceUnavailable,
                >= 500 => StatusCodes.Status502BadGateway,
                400 or 404 or 409 or 422 => ex.Status.Value,
                _ => StatusCodes.Status502BadGateway,
            };

            return ToErrorResult(statusCode, ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Cancelled,
                CancellationToken.None);
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Failed,
                CancellationToken.None);
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "execution_failed",
                ex.Message);
        }
    }

    internal static async Task<IResult> HandleCancelResponseAsync(
        HttpContext http,
        [FromRoute] string id,
        [FromServices] IResponsesCallerScopeResolver callerScopeResolver,
        [FromServices] IResponseSessionRegistrationPort responseSessionRegistrationPort,
        [FromServices] IResponseSessionQueryPort responseSessionQueryPort,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(callerScopeResolver);
        ArgumentNullException.ThrowIfNull(responseSessionRegistrationPort);
        ArgumentNullException.ThrowIfNull(responseSessionQueryPort);

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

        ResponsesCallerScope callerScope;
        try
        {
            callerScope = await callerScopeResolver.ResolveAsync(bearerToken, http, ct);
        }
        catch (ResponsesCallerScopeUnavailableException ex)
        {
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", ex.Message);
        }

        var snapshot = await responseSessionQueryPort.GetByResponseIdAsync(responseId, ct);
        var visibilityError = ValidateResponseVisibility(
            snapshot,
            callerScope,
            "response_not_found",
            "response id does not refer to a visible response session.");
        if (visibilityError is not null)
            return visibilityError;

        var visibleSnapshot = snapshot!;
        if (visibleSnapshot.Status == ResponseSessionStatus.Expired)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "response_expired",
                "response id refers to an expired response session.");
        }

        var cancelledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (visibleSnapshot.Status != ResponseSessionStatus.Cancelled)
        {
            try
            {
                await responseSessionRegistrationPort.UpdateStatusAsync(
                    visibleSnapshot.ActorId,
                    visibleSnapshot.ResponseId,
                    ResponseSessionStatus.Cancelled,
                    ct);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (InvalidOperationException ex)
            {
                return ToErrorResult(
                    StatusCodes.Status400BadRequest,
                    "response_cancel_rejected",
                    ex.Message);
            }
        }

        return Results.Json(new
        {
            id = visibleSnapshot.ResponseId,
            @object = "response",
            status = "cancelled",
            cancelled_at = cancelledAt,
        }, JsonOptions, statusCode: StatusCodes.Status200OK);
    }

    private static async Task WriteStreamResponseAsync(
        HttpResponse response,
        ILLMProviderFactory providerFactory,
        IResponseSessionRegistrationPort responseSessionRegistrationPort,
        ILogger logger,
        ResponseSessionRegistrationResult responseSession,
        LLMRequest request,
        NormalizedResponsesRequest normalized,
        ResponseSessionSnapshot? previousSnapshot,
        ResponsesToolClassification toolClassification,
        DateTimeOffset createdAtOffset,
        CancellationToken ct)
    {
        var createdAt = createdAtOffset.ToUnixTimeSeconds();
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(ct);

        var sequenceNumber = 0;
        var outputText = new StringBuilder();
        ResponsesUsage? usage = null;

        try
        {
            var provider = providerFactory.GetDefault();
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

            var completion = await StreamToolAwareCompletionAsync(
                response,
                provider,
                request,
                normalized,
                toolClassification,
                sequenceNumber,
                ct);
            sequenceNumber = completion.SequenceNumber;
            outputText.Append(completion.Text);
            usage = completion.Usage;

            var completedText = outputText.ToString();
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

            var completedToolCalls = completion.ForwardedToolCalls;
            await PersistForwardedToolCallsAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                toolClassification,
                completedToolCalls,
                DateTimeOffset.UtcNow,
                ct);
            await TryResolveIncomingToolResultsAsync(
                responseSessionRegistrationPort,
                logger,
                previousSnapshot,
                normalized,
                CancellationToken.None);

            var nextOutputIndex = 1;
            foreach (var toolCall in completedToolCalls)
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
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                completedText,
                completedToolCalls,
                usage);

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
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Completed,
                CancellationToken.None);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Failed,
                CancellationToken.None);
            await WriteStreamFailureAsync(
                response,
                normalized,
                createdAt,
                ++sequenceNumber,
                "authentication_required",
                ex.Message,
                ct);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Failed,
                CancellationToken.None);
            await WriteStreamFailureAsync(
                response,
                normalized,
                createdAt,
                ++sequenceNumber,
                ex.Kind.ToString().ToLowerInvariant(),
                ex.Message,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Cancelled,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                ResponseSessionStatus.Failed,
                CancellationToken.None);
            await WriteStreamFailureAsync(
                response,
                normalized,
                createdAt,
                ++sequenceNumber,
                "execution_failed",
                ex.Message,
                ct);
        }
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

    private static List<ChatMessage> BuildLlmMessages(
        NormalizedResponsesRequest normalized,
        ResponseSessionSnapshot? previousSnapshot)
    {
        var messages = new List<ChatMessage>();
        if (normalized.ToolResults.Count > 0 && previousSnapshot != null)
        {
            var toolCalls = BuildPreviousToolCalls(normalized, previousSnapshot);
            if (toolCalls.Count > 0)
            {
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    ToolCalls = toolCalls,
                });
            }

            foreach (var result in normalized.ToolResults)
                messages.Add(ChatMessage.Tool(result.CallId, result.Output));
        }

        if (!string.IsNullOrWhiteSpace(normalized.Prompt))
            messages.Add(ChatMessage.User(normalized.Prompt));

        return messages;
    }

    private static IReadOnlyList<ToolCall> BuildPreviousToolCalls(
        NormalizedResponsesRequest normalized,
        ResponseSessionSnapshot previousSnapshot)
    {
        var forwardedCalls = previousSnapshot.ForwardedToolCalls ?? [];
        var callsById = forwardedCalls
            .GroupBy(static call => call.CallId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var result = new List<ToolCall>();
        foreach (var input in normalized.ToolResults)
        {
            if (!callsById.TryGetValue(input.CallId, out var call))
                continue;

            result.Add(new ToolCall
            {
                Id = call.CallId,
                Name = call.ToolName,
                ArgumentsJson = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
            });
        }

        return result;
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

    private static string? ExtractChunkText(LLMStreamChunk chunk)
    {
        if (!string.IsNullOrWhiteSpace(chunk.DeltaContent))
            return chunk.DeltaContent;

        if (chunk.DeltaContentPart is { Kind: ContentPartKind.Text } part && !string.IsNullOrWhiteSpace(part.Text))
            return part.Text;

        return null;
    }

    private static async Task<IResult?> PersistIncomingToolResultsAsync(
        IResponseSessionRegistrationPort responseSessionRegistrationPort,
        ResponseSessionSnapshot previousSnapshot,
        NormalizedResponsesRequest normalized,
        CancellationToken ct)
    {
        var callsById = (previousSnapshot.ForwardedToolCalls ?? [])
            .GroupBy(static call => call.CallId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        foreach (var result in normalized.ToolResults)
        {
            if (!callsById.TryGetValue(result.CallId, out var call))
            {
                return ToErrorResult(
                    StatusCodes.Status400BadRequest,
                    "tool_call_not_found",
                    $"previous_response_id has no forwarded tool call '{result.CallId}'.");
            }

            var schemaHash = result.SchemaHash ?? call.SchemaHash;
            if (!string.Equals(call.SchemaHash, schemaHash, StringComparison.Ordinal))
            {
                return ToErrorResult(
                    StatusCodes.Status400BadRequest,
                    "tool_schema_hash_mismatch",
                    $"Forwarded tool call '{result.CallId}' schema hash mismatch.");
            }

            if (call.Status == ResponseSessionForwardedToolCallStatus.Resolved)
                continue;

            if (call.Status is ResponseSessionForwardedToolCallStatus.Cancelled
                or ResponseSessionForwardedToolCallStatus.Expired)
            {
                return ToErrorResult(
                    StatusCodes.Status400BadRequest,
                    "tool_call_not_available",
                    $"Forwarded tool call '{result.CallId}' is {call.Status} and cannot receive a result.");
            }

            try
            {
                await responseSessionRegistrationPort.ReceiveForwardedToolResultAsync(
                    previousSnapshot.ActorId,
                    previousSnapshot.ResponseId,
                    result.CallId,
                    schemaHash,
                    result.Output,
                    ct);
            }
            catch (InvalidOperationException ex)
            {
                return ToErrorResult(
                    StatusCodes.Status400BadRequest,
                    "tool_result_rejected",
                    ex.Message);
            }
        }

        return null;
    }

    private static bool TryBuildAlreadyResolvedToolResultResponse(
        NormalizedResponsesRequest normalized,
        ResponseSessionSnapshot previousSnapshot,
        [NotNullWhen(true)] out IResult? result)
    {
        result = null;
        if (normalized.ToolResults.Count == 0)
            return false;

        var callsById = (previousSnapshot.ForwardedToolCalls ?? [])
            .GroupBy(static call => call.CallId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var resolvedOutputs = new List<string>();
        foreach (var input in normalized.ToolResults)
        {
            if (!callsById.TryGetValue(input.CallId, out var call) ||
                call.Status != ResponseSessionForwardedToolCallStatus.Resolved)
            {
                return false;
            }

            var schemaHash = input.SchemaHash ?? call.SchemaHash;
            if (!string.Equals(call.SchemaHash, schemaHash, StringComparison.Ordinal))
            {
                result = ToErrorResult(
                    StatusCodes.Status400BadRequest,
                    "tool_schema_hash_mismatch",
                    $"Forwarded tool call '{input.CallId}' schema hash mismatch.");
                return true;
            }

            resolvedOutputs.Add(string.IsNullOrWhiteSpace(call.ResultJson) ? input.Output : call.ResultJson!);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var outputText = resolvedOutputs.Count == 1
            ? resolvedOutputs[0]
            : JsonSerializer.Serialize(resolvedOutputs, JsonOptions);
        result = Results.Json(
            BuildCompletedResponse(
                normalized,
                now,
                now,
                outputText,
                [],
                null),
            JsonOptions,
            statusCode: StatusCodes.Status200OK);
        return true;
    }

    private static async Task TryResolveIncomingToolResultsAsync(
        IResponseSessionRegistrationPort responseSessionRegistrationPort,
        ILogger logger,
        ResponseSessionSnapshot? previousSnapshot,
        NormalizedResponsesRequest normalized,
        CancellationToken ct)
    {
        if (previousSnapshot is null || normalized.ToolResults.Count == 0)
            return;

        foreach (var callId in normalized.ToolResults
                     .Select(static result => result.CallId)
                     .Where(static callId => !string.IsNullOrWhiteSpace(callId))
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                await responseSessionRegistrationPort.ResolveForwardedToolResultAsync(
                    previousSnapshot.ActorId,
                    previousSnapshot.ResponseId,
                    callId,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to mark forwarded Responses tool call {CallId} as resolved for response {ResponseId}.",
                    callId,
                    previousSnapshot.ResponseId);
            }
        }
    }

    private static async Task PersistForwardedToolCallsAsync(
        IResponseSessionRegistrationPort responseSessionRegistrationPort,
        ILogger logger,
        ResponseSessionRegistrationResult responseSession,
        ResponsesToolClassification toolClassification,
        IReadOnlyList<ToolCall> toolCalls,
        DateTimeOffset emittedAt,
        CancellationToken ct)
    {
        if (toolCalls.Count == 0)
            return;

        var declarations = toolClassification.ForwardedTools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        var expiry = emittedAt.AddHours(24);
        foreach (var toolCall in toolCalls)
        {
            if (string.IsNullOrWhiteSpace(toolCall.Id))
                throw new InvalidOperationException("Forwarded tool call is missing call_id.");
            if (string.IsNullOrWhiteSpace(toolCall.Name))
                throw new InvalidOperationException($"Forwarded tool call '{toolCall.Id}' is missing tool name.");
            if (!declarations.TryGetValue(toolCall.Name, out var declaration))
            {
                throw new InvalidOperationException(
                    $"Forwarded tool call '{toolCall.Id}' references undeclared tool '{toolCall.Name}'.");
            }

            var call = new ResponseSessionForwardedToolCall
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                SchemaHash = declaration.SchemaHash,
                ArgumentsJson = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson,
                Status = ResponseSessionForwardedToolCallStatus.Pending,
                EmittedAt = Timestamp.FromDateTimeOffset(emittedAt),
                Expiry = Timestamp.FromDateTimeOffset(expiry),
            };

            await responseSessionRegistrationPort.RecordForwardedToolCallAsync(
                responseSession.ActorId,
                responseSession.ResponseId,
                call,
                ct);
            logger.LogDebug(
                "Persisted forwarded Responses tool call {CallId} for response {ResponseId}.",
                toolCall.Id,
                responseSession.ResponseId);
        }
    }

    private static IReadOnlyList<ToolCall> SelectForwardedToolCalls(
        IReadOnlyList<ToolCall> toolCalls,
        ResponsesToolClassification toolClassification)
    {
        if (toolCalls.Count == 0 || toolClassification.ForwardedTools.Count == 0)
            return [];

        var forwardedToolNames = toolClassification.ForwardedTools
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        return toolCalls
            .Where(call => forwardedToolNames.Contains(call.Name))
            .ToArray();
    }

    private sealed record ResponsesCompletionResult(
        string Text,
        ResponsesUsage? Usage,
        IReadOnlyList<ToolCall> ForwardedToolCalls);

    private sealed record ResponsesStreamCompletionResult(
        string Text,
        ResponsesUsage? Usage,
        IReadOnlyList<ToolCall> ForwardedToolCalls,
        int SequenceNumber);

    private static async Task<ResponsesCompletionResult> CollectToolAwareCompletionAsync(
        ILLMProvider provider,
        LLMRequest request,
        ResponsesToolClassification toolClassification,
        CancellationToken ct)
    {
        var messages = request.Messages.ToList();
        var outputText = new StringBuilder();
        ResponsesUsage? usage = null;

        for (var round = 0; round < 8; round++)
        {
            var roundRequest = CloneRequestWithMessages(request, messages);
            var (roundText, roundUsage, toolCalls) = await CollectStreamCompletionAsync(provider, roundRequest, ct);
            outputText.Append(roundText);
            usage = roundUsage ?? usage;

            var forwardedToolCalls = SelectForwardedToolCalls(toolCalls, toolClassification);
            if (forwardedToolCalls.Count > 0)
                return new ResponsesCompletionResult(outputText.ToString(), usage, forwardedToolCalls);

            var localToolCalls = SelectLocalToolCalls(toolCalls, toolClassification);
            if (localToolCalls.Count == 0)
                return new ResponsesCompletionResult(outputText.ToString(), usage, []);

            messages.Add(new ChatMessage
            {
                Role = "assistant",
                ToolCalls = localToolCalls,
            });
            await ExecuteLocalToolCallsAsync(request, localToolCalls, messages, ct);
        }

        return new ResponsesCompletionResult(outputText.ToString(), usage, []);
    }

    private static async Task<ResponsesStreamCompletionResult> StreamToolAwareCompletionAsync(
        HttpResponse response,
        ILLMProvider provider,
        LLMRequest request,
        NormalizedResponsesRequest normalized,
        ResponsesToolClassification toolClassification,
        int sequenceNumber,
        CancellationToken ct)
    {
        var messages = request.Messages.ToList();
        var outputText = new StringBuilder();
        ResponsesUsage? usage = null;

        for (var round = 0; round < 8; round++)
        {
            var roundRequest = CloneRequestWithMessages(request, messages);
            var toolCalls = new ResponsesToolCallAccumulator();
            var previousMetadata = AgentToolRequestContext.CurrentMetadata;
            try
            {
                AgentToolRequestContext.CurrentMetadata = roundRequest.Metadata;
                await foreach (var chunk in provider.ChatStreamAsync(roundRequest, ct))
                {
                    var delta = ExtractChunkText(chunk);
                    if (!string.IsNullOrEmpty(delta))
                    {
                        outputText.Append(delta);
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
                            ct);
                    }

                    if (chunk.DeltaToolCall != null)
                        toolCalls.TrackDelta(chunk.DeltaToolCall);

                    if (chunk.Usage != null)
                        usage = MapUsage(chunk.Usage);

                    if (chunk.IsLast)
                        break;
                }
            }
            finally
            {
                AgentToolRequestContext.CurrentMetadata = previousMetadata;
            }

            var builtToolCalls = toolCalls.BuildToolCalls();
            var forwardedToolCalls = SelectForwardedToolCalls(builtToolCalls, toolClassification);
            if (forwardedToolCalls.Count > 0)
            {
                return new ResponsesStreamCompletionResult(
                    outputText.ToString(),
                    usage,
                    forwardedToolCalls,
                    sequenceNumber);
            }

            var localToolCalls = SelectLocalToolCalls(builtToolCalls, toolClassification);
            if (localToolCalls.Count == 0)
            {
                return new ResponsesStreamCompletionResult(
                    outputText.ToString(),
                    usage,
                    [],
                    sequenceNumber);
            }

            messages.Add(new ChatMessage
            {
                Role = "assistant",
                ToolCalls = localToolCalls,
            });
            await ExecuteLocalToolCallsAsync(request, localToolCalls, messages, ct);
        }

        return new ResponsesStreamCompletionResult(outputText.ToString(), usage, [], sequenceNumber);
    }

    private static LLMRequest CloneRequestWithMessages(
        LLMRequest request,
        List<ChatMessage> messages) =>
        new()
        {
            Messages = [.. messages],
            RequestId = request.RequestId,
            Metadata = request.Metadata,
            Tools = request.Tools,
            Model = request.Model,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            ResponseFormat = request.ResponseFormat,
        };

    private static IReadOnlyList<ToolCall> SelectLocalToolCalls(
        IReadOnlyList<ToolCall> toolCalls,
        ResponsesToolClassification toolClassification)
    {
        if (toolCalls.Count == 0 || toolClassification.EffectiveTools.Count == 0)
            return [];

        var forwardedToolNames = toolClassification.ForwardedTools
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var localToolNames = toolClassification.EffectiveTools
            .Select(static tool => tool.Name)
            .Where(name => !forwardedToolNames.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        return toolCalls
            .Where(call => localToolNames.Contains(call.Name))
            .ToArray();
    }

    private static async Task ExecuteLocalToolCallsAsync(
        LLMRequest request,
        IReadOnlyList<ToolCall> toolCalls,
        List<ChatMessage> messages,
        CancellationToken ct)
    {
        if (request.Tools is not { Count: > 0 })
            return;

        var toolsByName = request.Tools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var previousMetadata = AgentToolRequestContext.CurrentMetadata;
        try
        {
            AgentToolRequestContext.CurrentMetadata = request.Metadata;
            foreach (var toolCall in toolCalls)
            {
                var result = toolsByName.TryGetValue(toolCall.Name, out var tool)
                    ? await tool.ExecuteAsync(
                        string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson,
                        ct)
                    : JsonSerializer.Serialize(new
                    {
                        error = "aevatar_substitute_tool_not_registered",
                        tool_name = toolCall.Name,
                    });
                messages.Add(ChatMessage.Tool(toolCall.Id, result));
            }
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = previousMetadata;
        }
    }

    private static async Task<(string Text, ResponsesUsage? Usage, IReadOnlyList<ToolCall> ToolCalls)> CollectStreamCompletionAsync(
        ILLMProvider provider,
        LLMRequest request,
        CancellationToken ct)
    {
        var outputText = new StringBuilder();
        var toolCalls = new ResponsesToolCallAccumulator();
        ResponsesUsage? usage = null;

        var previousMetadata = AgentToolRequestContext.CurrentMetadata;
        try
        {
            AgentToolRequestContext.CurrentMetadata = request.Metadata;
            await foreach (var chunk in provider.ChatStreamAsync(request, ct))
            {
                var delta = ExtractChunkText(chunk);
                if (!string.IsNullOrEmpty(delta))
                    outputText.Append(delta);

                if (chunk.DeltaToolCall != null)
                    toolCalls.TrackDelta(chunk.DeltaToolCall);

                if (chunk.Usage != null)
                    usage = MapUsage(chunk.Usage);

                if (chunk.IsLast)
                    break;
            }
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = previousMetadata;
        }

        return (outputText.ToString(), usage, toolCalls.BuildToolCalls());
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

    private static ResponseSessionRecord BuildResponseSessionRecord(
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt)
    {
        return new ResponseSessionRecord
        {
            ResponseId = normalized.ResponseId,
            ScopeId = callerScope.ScopeId,
            OwnerSubject = callerScope.OwnerSubject,
            OriginKind = callerScope.OriginKind,
            PreviousResponseId = normalized.PreviousResponseId ?? string.Empty,
            Status = ResponseSessionStatus.Accepted,
            CreatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            UpdatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };
    }

    private static IResult? ValidatePreviousResponse(
        ResponseSessionSnapshot? previous,
        ResponsesCallerScope callerScope)
    {
        var visibilityError = ValidateResponseVisibility(
            previous,
            callerScope,
            "previous_response_not_found",
            "previous_response_id does not refer to a visible response session.");
        if (visibilityError is not null)
            return visibilityError;

        var visiblePrevious = previous!;
        if (visiblePrevious.Ttl > TimeSpan.Zero &&
            visiblePrevious.CreatedAt.Add(visiblePrevious.Ttl) <= DateTimeOffset.UtcNow)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "previous_response_expired",
                "previous_response_id refers to an expired response session.");
        }

        if (visiblePrevious.Status is ResponseSessionStatus.Cancelled
            or ResponseSessionStatus.Expired
            or ResponseSessionStatus.Failed)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "previous_response_not_available",
                "previous_response_id refers to a response session that cannot be continued.");
        }

        return null;
    }

    private static IResult? ValidateResponseVisibility(
        ResponseSessionSnapshot? response,
        ResponsesCallerScope callerScope,
        string notFoundCode,
        string notFoundMessage)
    {
        if (response is null)
        {
            return ToErrorResult(
                StatusCodes.Status404NotFound,
                notFoundCode,
                notFoundMessage);
        }

        if (!string.Equals(response.ScopeId, callerScope.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(response.OwnerSubject, callerScope.OwnerSubject, StringComparison.Ordinal))
        {
            return ToErrorResult(
                StatusCodes.Status403Forbidden,
                "response_scope_mismatch",
                "response id is not visible to the current caller scope.");
        }

        if (response.OriginKind != callerScope.OriginKind)
        {
            return ToErrorResult(
                StatusCodes.Status403Forbidden,
                "response_origin_mismatch",
                "response id origin does not match the current ingress origin.");
        }

        return null;
    }

    private static async Task TryUpdateSessionStatusAsync(
        IResponseSessionRegistrationPort responseSessionRegistrationPort,
        ILogger logger,
        ResponseSessionRegistrationResult responseSession,
        ResponseSessionStatus status,
        CancellationToken ct)
    {
        try
        {
            await responseSessionRegistrationPort.UpdateStatusAsync(
                responseSession.ActorId,
                responseSession.ResponseId,
                status,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The response session has already been accepted. Completion markers are
            // observable state, but they must not leak persistence failures or secrets
            // into the Responses payload path.
            logger.LogWarning(
                ex,
                "Failed to update response session {ResponseId} to {Status}.",
                responseSession.ResponseId,
                status);
        }
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
}
