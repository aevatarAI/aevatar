using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
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
        // Auth is endpoint-internal: each handler manually extracts the inbound
        // bearer and resolves the caller via NyxID `/me`. Opt out of the host's
        // FallbackPolicy.RequireAuthenticatedUser() so opaque NyxID API keys
        // (non-JWT) reach the handler instead of being 401'd by JwtBearer.
        group.MapPost("/responses", HandleCreateResponseAsync).AllowAnonymous();
        group.MapPost("/responses/{id}/cancel", HandleCancelResponseAsync).AllowAnonymous();
        group.MapGet("/models", HandleListModelsAsync).AllowAnonymous();
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
        [FromServices] IResponsesRouteResolver routeResolver,
        [FromServices] ILlmSessionRegistrationPort responseSessionRegistrationPort,
        [FromServices] ILlmSessionQueryPort responseSessionQueryPort,
        [FromServices] IResponsesCompletionApplicationService completionService,
        [FromServices] IEnumerable<IResponsesToolProvider> toolProviders,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(callerScopeResolver);
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(responseSessionRegistrationPort);
        ArgumentNullException.ThrowIfNull(responseSessionQueryPort);
        ArgumentNullException.ThrowIfNull(completionService);
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

        LlmSessionSnapshot? previousSnapshot = null;
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
        LlmSessionRegistrationResult responseSession;
        try
        {
            responseSession = await responseSessionRegistrationPort.RegisterAsync(
                BuildResponseSessionRecord(normalized, callerScope, createdAt),
                ct);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var correlation = LogAndCorrelate(logger, ex, "session_registration", normalized.ResponseId);
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "session_registration_failed",
                $"Failed to register response session. Correlation: {correlation}");
        }

        var toolProviderContext = BuildToolProviderContext(callerScope, normalized.ResponseId, bearerToken);
        var toolClassification = await ResponsesToolClassifier.ClassifyAsync(
            normalized.DeclaredTools.Select(ToApplicationToolDeclaration).ToArray(),
            toolProviders,
            toolProviderContext,
            logger,
            ct);
        // OpenRouter-style vendor prefix: the catalog advertises every model as
        // `{slug}/{model}` regardless of route shape (gateway provider, user
        // service, proxy service). When the slug resolves to a known catalog
        // entry, pin its RouteValue (full path — e.g. `/api/v1/llm/anthropic/v1`
        // for gateway providers, `/api/v1/proxy/s/<slug>` for proxy services)
        // as the per-request route preference so NyxIdLLMProvider routes to
        // the right plane. An unknown slug (catalog miss, or a model name that
        // just happens to contain `/`) falls through to default gateway routing
        // with the model string preserved verbatim — NyxID's gateway picks the
        // backend by model name.
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

        // LLMRequest.Metadata flows into the LLM provider, where its values may be
        // serialized into logs, traces, or third-party SDKs. Keep only safe-to-log
        // tracing/config values here. Business-control identity and per-request
        // credentials live on the typed CallerContext below; the LLM provider
        // (e.g. NyxIdLLMProvider) reads the bearer from Credentials, not Metadata.
        var llmMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = normalized.ResponseId,
            [ChannelMetadataKeys.RegistrationScopeId] = callerScope.ScopeId,
        };
        if (resolvedRouteValue is not null)
            llmMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = resolvedRouteValue;
        var toolContextMetadata = toolProviderContext.ToolContextMetadata;

        var llmRequest = new LLMRequest
        {
            Messages = BuildLlmMessages(normalized, previousSnapshot),
            RequestId = normalized.ResponseId,
            Metadata = llmMetadata,
            CallerContext = new LLMRequestCallerContext(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                normalized.ResponseId,
                new LLMRequestCallerCredentials(bearerToken)),
            Tools = toolClassification.EffectiveTools,
            // LLM provider receives the bare model name (vendor prefix already
            // consumed into NyxIdRoutePreference above). Response-snapshot
            // echoes still use normalized.Model so the client sees back what it sent.
            Model = effectiveModel,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxOutputTokens,
        };

        if (normalized.Stream)
        {
            await WriteStreamResponseAsync(
                http.Response,
                providerFactory,
                completionService,
                responseSessionRegistrationPort,
                logger,
                responseSession,
                llmRequest,
                toolContextMetadata,
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
            var completion = await completionService.CollectAsync(
                provider,
                llmRequest,
                toolContextMetadata,
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
                ct);
            var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Completed,
                ct);
            var completed = BuildCompletedResponse(
                normalized,
                createdAt.ToUnixTimeSeconds(),
                completedAt,
                completion.Text,
                forwardedToolCalls,
                completion.Usage is null ? null : MapUsage(completion.Usage));
            return Results.Json(completed, statusCode: StatusCodes.Status200OK);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Failed,
                CancellationToken.None);
            // Authentication failure messages from NyxID are intentionally surfaced
            // — they describe why the caller's own token was rejected and don't
            // contain server-side internals.
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Failed,
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

            var correlation = LogAndCorrelate(logger, ex, "nyxid_upstream", normalized.ResponseId);
            return ToErrorResult(
                statusCode,
                ex.Kind.ToString().ToLowerInvariant(),
                $"Upstream provider error. Correlation: {correlation}");
        }
        catch (OperationCanceledException)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Cancelled,
                CancellationToken.None);
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Failed,
                CancellationToken.None);
            var correlation = LogAndCorrelate(logger, ex, "execution", normalized.ResponseId);
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "execution_failed",
                $"Execution failed. Correlation: {correlation}");
        }
    }

    private static string LogAndCorrelate(
        ILogger logger,
        Exception ex,
        string stage,
        string responseId)
    {
        var correlation = Guid.NewGuid().ToString("N")[..16];
        logger.LogError(
            ex,
            "Responses {Stage} failure for {ResponseId} (correlation {Correlation}).",
            stage,
            responseId,
            correlation);
        return correlation;
    }

    internal static async Task<IResult> HandleCancelResponseAsync(
        HttpContext http,
        [FromRoute] string id,
        [FromServices] IResponsesCallerScopeResolver callerScopeResolver,
        [FromServices] ILlmSessionRegistrationPort responseSessionRegistrationPort,
        [FromServices] ILlmSessionQueryPort responseSessionQueryPort,
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
        if (visibleSnapshot.Status == LlmSessionStatus.Expired)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "response_expired",
                "response id refers to an expired response session.");
        }

        var cancelledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (visibleSnapshot.Status != LlmSessionStatus.Cancelled)
        {
            try
            {
                await responseSessionRegistrationPort.UpdateStatusAsync(
                    visibleSnapshot.ActorId,
                    visibleSnapshot.ResponseId,
                    LlmSessionStatus.Cancelled,
                    ct);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(StatusCodes.Status408RequestTimeout);
            }
            catch (InvalidOperationException ex)
            {
                // InvalidOperationException here originates from the actor's
                // own validation messages (e.g. terminal-state guard). They're
                // safe to surface — they describe the protocol violation, not
                // server internals.
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
        IResponsesCompletionApplicationService completionService,
        ILlmSessionRegistrationPort responseSessionRegistrationPort,
        ILogger logger,
        LlmSessionRegistrationResult responseSession,
        LLMRequest request,
        IReadOnlyDictionary<string, string> toolContextMetadata,
        NormalizedResponsesRequest normalized,
        LlmSessionSnapshot? previousSnapshot,
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
        TokenUsage? usage = null;

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

            var completion = await completionService.StreamAsync(
                provider,
                request,
                toolContextMetadata,
                toolClassification,
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
            usage = completion.Usage;

            var completedText = completion.Text;
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
                ct);

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
                usage is null ? null : MapUsage(usage));

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
                LlmSessionStatus.Completed,
                ct);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Failed,
                CancellationToken.None);
            // NyxID authentication-required messages describe why the caller's
            // token was rejected; surface verbatim (not server internals).
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
                LlmSessionStatus.Failed,
                CancellationToken.None);
            var correlation = LogAndCorrelate(logger, ex, "stream_nyxid_upstream", normalized.ResponseId);
            await WriteStreamFailureAsync(
                response,
                normalized,
                createdAt,
                ++sequenceNumber,
                ex.Kind.ToString().ToLowerInvariant(),
                $"Upstream provider error. Correlation: {correlation}",
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Cancelled,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Failed,
                CancellationToken.None);
            var correlation = LogAndCorrelate(logger, ex, "stream_execution", normalized.ResponseId);
            await WriteStreamFailureAsync(
                response,
                normalized,
                createdAt,
                ++sequenceNumber,
                "execution_failed",
                $"Execution failed. Correlation: {correlation}",
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
        LlmSessionSnapshot? previousSnapshot)
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
        LlmSessionSnapshot previousSnapshot)
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

    private static ResponsesApplicationToolDeclaration ToApplicationToolDeclaration(
        ResponsesToolDeclaration declaration) =>
        new(
            declaration.Name,
            declaration.Description,
            declaration.ParametersJson,
            declaration.SchemaHash);

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

    private static async Task<IResult?> PersistIncomingToolResultsAsync(
        ILlmSessionRegistrationPort responseSessionRegistrationPort,
        LlmSessionSnapshot previousSnapshot,
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

            if (call.Status == LlmSessionForwardedToolCallStatus.Resolved)
                continue;

            if (call.Status is LlmSessionForwardedToolCallStatus.Cancelled
                or LlmSessionForwardedToolCallStatus.Expired)
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
        LlmSessionSnapshot previousSnapshot,
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
                call.Status != LlmSessionForwardedToolCallStatus.Resolved)
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
        ILlmSessionRegistrationPort responseSessionRegistrationPort,
        ILogger logger,
        LlmSessionSnapshot? previousSnapshot,
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
        ILlmSessionRegistrationPort responseSessionRegistrationPort,
        ILogger logger,
        LlmSessionRegistrationResult responseSession,
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

            var argumentsJson = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson;
            var call = new LlmSessionForwardedToolCall
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                SchemaHash = declaration.SchemaHash,
                Arguments = ResponsesJsonValues.ParseBoundaryPayload(argumentsJson),
                Status = LlmSessionForwardedToolCallStatus.Pending,
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
            new ResponsesToolProviderCallerScope(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                callerScope.OriginKind.ToString()),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.RequestId] = responseId,
                [LLMRequestMetadataKeys.ResponseId] = responseId,
                [LLMRequestMetadataKeys.ScopeId] = callerScope.ScopeId,
                [LLMRequestMetadataKeys.OwnerSubject] = callerScope.OwnerSubject,
                [ChannelMetadataKeys.RegistrationScopeId] = callerScope.ScopeId,
                [LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken,
            });
    }

    private static LlmSessionRecord BuildResponseSessionRecord(
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt)
    {
        return new LlmSessionRecord
        {
            ResponseId = normalized.ResponseId,
            ScopeId = callerScope.ScopeId,
            OwnerSubject = callerScope.OwnerSubject,
            OriginKind = callerScope.OriginKind,
            PreviousResponseId = normalized.PreviousResponseId ?? string.Empty,
            Status = LlmSessionStatus.Accepted,
            CreatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            UpdatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };
    }

    private static IResult? ValidatePreviousResponse(
        LlmSessionSnapshot? previous,
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

        if (visiblePrevious.Status is LlmSessionStatus.Cancelled
            or LlmSessionStatus.Expired
            or LlmSessionStatus.Failed)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "previous_response_not_available",
                "previous_response_id refers to a response session that cannot be continued.");
        }

        return null;
    }

    private static IResult? ValidateResponseVisibility(
        LlmSessionSnapshot? response,
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
        ILlmSessionRegistrationPort responseSessionRegistrationPort,
        ILogger logger,
        LlmSessionRegistrationResult responseSession,
        LlmSessionStatus status,
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
