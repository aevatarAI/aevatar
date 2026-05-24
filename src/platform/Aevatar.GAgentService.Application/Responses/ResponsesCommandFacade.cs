using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Mainnet Host endpoints owned normalization, target resolution, session registration, tool persistence, and LLM command execution inline.
//   New principle: Application owns the Responses command lifecycle as a typed facade; Host maps HTTP/SSE/JSON frames around these command plans and results.
// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: ForwardToTeam/ForwardToGAgent skipped session lifecycle; Host new'd StringBuilder/Dictionary/List<ToolCall> to synthesize response.completed
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public sealed class ResponsesCommandFacade(
    ILLMProviderFactory providerFactory,
    IResponsesCallerScopeResolver callerScopeResolver,
    IResponsesChatRouteDecisionPort chatRouteDecisionPort,
    IResponsesRouteResolver routeResolver,
    ILlmSessionRegistrationPort responseSessionRegistrationPort,
    ILlmSessionQueryPort responseSessionQueryPort,
    IResponsesCompletionApplicationService completionService,
    IEnumerable<IResponsesToolProvider> toolProviders,
    IToolSetRegistry toolSetRegistry,
    ILogger<ResponsesCommandFacade> logger) : IResponsesCommandFacade
{
    private const string RegistrationScopeMetadataKey = "scope_id";

    public async Task<ResponsesCreateCommandResult> CreateAsync(
        ResponsesCommandRequest request,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedResult = ResponsesRequestNormalizer.Normalize(request);
        if (!normalizedResult.Succeeded)
        {
            return ResponsesCreateCommandResult.FromError(
                400,
                normalizedResult.ErrorCode ?? "invalid_request_error",
                normalizedResult.ErrorMessage ?? "Invalid request.");
        }

        var normalized = normalizedResult.Request!;
        var callerScopeResult = await ResolveCallerScopeAsync(bearerToken, ct);
        if (callerScopeResult.Error is not null)
            return ResponsesCreateCommandResult.FromError(
                callerScopeResult.Error.StatusCode,
                callerScopeResult.Error.Code,
                callerScopeResult.Error.Message);

        var callerScope = callerScopeResult.Scope!;
        var routedModelResult = await ResolveRouteTargetAsync(normalized, callerScope, ct);
        if (routedModelResult.Error is not null)
            return ResponsesCreateCommandResult.FromError(
                routedModelResult.Error.StatusCode,
                routedModelResult.Error.Code,
                routedModelResult.Error.Message);
        var continuation = await PrepareContinuationAsync(normalized, callerScope, ct);
        if (continuation.Error is not null)
            return ResponsesCreateCommandResult.FromError(
                continuation.Error.StatusCode,
                continuation.Error.Code,
                continuation.Error.Message);
        if (continuation.AlreadyResolved is not null)
            return ResponsesCreateCommandResult.FromCompleted(continuation.AlreadyResolved);

        var createdAt = DateTimeOffset.UtcNow;
        var sessionResult = await RegisterSessionAsync(normalized, callerScope, createdAt, ct);
        if (sessionResult.Error is not null)
            return ResponsesCreateCommandResult.FromError(
                sessionResult.Error.StatusCode,
                sessionResult.Error.Code,
                sessionResult.Error.Message);

        // Refactor (iter75/cluster-075-responses-agui-host-completion-state):
        //   Old pattern: ForwardToTeam/ForwardToGAgent skipped session lifecycle; Host new'd StringBuilder/Dictionary/List<ToolCall> to synthesize response.completed
        //   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
        if (routedModelResult.ForwardAction is not null)
            return ResponsesCreateCommandResult.FromForward(new ResponsesForwardCommandResult(
                normalized,
                callerScope,
                routedModelResult.ForwardAction,
                sessionResult.Session!,
                continuation.PreviousSnapshot,
                createdAt));

        var prepared = await BuildExecutionPlanAsync(
            normalized,
            continuation.PreviousSnapshot,
            callerScope,
            routedModelResult.Action!,
            bearerToken,
            sessionResult.Session!,
            createdAt,
            ct);
        if (prepared.Error is not null)
            return ResponsesCreateCommandResult.FromError(
                prepared.Error.StatusCode,
                prepared.Error.Code,
                prepared.Error.Message);

        return normalized.Stream
            ? ResponsesCreateCommandResult.FromStreamPlan(prepared.Plan!)
            : await ExecuteNonStreamingAsync(prepared.Plan!, ct);
    }

    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Response cancellation resolved caller/query/write state inside the Minimal API handler.
    //   New principle: Application validates visibility and advances session status; Host maps the typed result to HTTP.
    public async Task<ResponsesCancelCommandResult> CancelAsync(
        string responseId,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseId);

        var callerScopeResult = await ResolveCallerScopeAsync(bearerToken, ct);
        if (callerScopeResult.Error is not null)
            return ResponsesCancelCommandResult.FromError(
                callerScopeResult.Error.StatusCode,
                callerScopeResult.Error.Code,
                callerScopeResult.Error.Message);

        var snapshot = await responseSessionQueryPort.GetByResponseIdAsync(responseId, ct);
        var visibilityError = ValidateResponseVisibility(
            snapshot,
            callerScopeResult.Scope!,
            "response_not_found",
            "response id does not refer to a visible response session.");
        if (visibilityError is not null)
            return ResponsesCancelCommandResult.FromError(visibilityError.StatusCode, visibilityError.Code, visibilityError.Message);

        var visibleSnapshot = snapshot!;
        if (visibleSnapshot.Status == LlmSessionStatus.Expired)
        {
            return ResponsesCancelCommandResult.FromError(
                400,
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
                return ResponsesCancelCommandResult.FromError(408, "request_timeout", "Request timed out.");
            }
            catch (InvalidOperationException ex)
            {
                return ResponsesCancelCommandResult.FromError(400, "response_cancel_rejected", ex.Message);
            }
        }

        return ResponsesCancelCommandResult.FromCancelled(visibleSnapshot.ResponseId, cancelledAt);
    }

    public async Task<ResponsesStreamCommandResult> StreamAsync(
        ResponsesCreateCommandPlan plan,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(onTextDelta);

        try
        {
            var completion = await StreamWithToolChoiceHintAsync(plan, onTextDelta, ct);
            await PersistForwardedToolCallsAsync(
                plan.Session,
                plan.ToolClassification,
                completion.ForwardedToolCalls,
                DateTimeOffset.UtcNow,
                ct);
            await TryResolveIncomingToolResultsAsync(plan.PreviousSnapshot, plan.Normalized, ct);
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Completed, ct);
            return ResponsesStreamCommandResult.FromCompleted(
                completion.Text,
                completion.ForwardedToolCalls,
                completion.Usage);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(401, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(ex.Status ?? 502, ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(408, "request_timeout", "Request timed out.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Streaming /v1/responses {ResponseId} failed", plan.Normalized.ResponseId);
            return ResponsesStreamCommandResult.FromError(500, "api_error", "Internal server error.");
        }
    }

    private async Task<ResponsesCompletionResult> StreamWithToolChoiceHintAsync(
        ResponsesCreateCommandPlan plan,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken ct)
    {
        var provider = providerFactory.GetDefault();
        using (ResponsesToolContext.Push(plan.ToolChoiceHintPlan))
        {
            return await completionService.StreamAsync(
                provider,
                plan.LlmRequest,
                plan.ToolContextMetadata,
                plan.ToolClassification,
                onTextDelta,
                ct);
        }
    }

    private async Task<CallerScopeResult> ResolveCallerScopeAsync(string bearerToken, CancellationToken ct)
    {
        try
        {
            var callerScope = await callerScopeResolver.ResolveAsync(bearerToken, ct);
            return new CallerScopeResult(callerScope, null);
        }
        catch (ResponsesCallerScopeUnavailableException ex)
        {
            return new CallerScopeResult(null, new ResponsesCommandError(401, "authentication_required", ex.Message));
        }
    }

    private async Task<RouteTargetResult> ResolveRouteTargetAsync(
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope,
        CancellationToken ct)
    {
        var routeDecision = await ResolveResponsesChatRouteAsync(
            callerScope,
            normalized.Model,
            ResolveToolMode(normalized.DeclaredTools.Count, normalized.ToolResults.Count),
            BuildContentHint(normalized.Prompt),
            ct);

        if (routeDecision.Action.Reject is not null)
        {
            return RouteTargetResult.FromError(
                403,
                "chat_route_rejected",
                string.IsNullOrWhiteSpace(routeDecision.Action.Reject.Reason)
                    ? "The chat route policy rejected this request."
                    : routeDecision.Action.Reject.Reason);
        }

        if (routeDecision.Action.ForwardToTeam is not null ||
            routeDecision.Action.ForwardToGagent is not null)
        {
            return RouteTargetResult.FromForward(routeDecision.Action);
        }

        var action = routeDecision.Action.Clone();
        var routedModel = !string.IsNullOrWhiteSpace(action.ForwardToModel?.ModelName)
            ? action.ForwardToModel.ModelName.Trim()
            : normalized.Model;
        if (action.ForwardToModel is null)
        {
            action.ForwardToModel = new ForwardToModel();
        }

        action.ForwardToModel.ModelName = routedModel;
        return RouteTargetResult.FromModel(action);
    }

    private async Task<ContinuationResult> PrepareContinuationAsync(
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope,
        CancellationToken ct)
    {
        LlmSessionSnapshot? previousSnapshot = null;
        if (normalized.PreviousResponseId is not null)
        {
            previousSnapshot = await responseSessionQueryPort.GetByResponseIdAsync(normalized.PreviousResponseId, ct);
            var previousError = ValidatePreviousResponse(previousSnapshot, callerScope);
            if (previousError is not null)
                return ContinuationResult.FromError(previousError);
        }

        if (normalized.ToolResults.Count > 0 && previousSnapshot is null)
        {
            return ContinuationResult.FromError(new ResponsesCommandError(
                400,
                "previous_response_required",
                "function_call_output requires previous_response_id."));
        }

        if (previousSnapshot is not null &&
            TryBuildAlreadyResolvedToolResultResponse(normalized, previousSnapshot, out var alreadyResolvedResult, out var alreadyResolvedError))
        {
            return alreadyResolvedError is not null
                ? ContinuationResult.FromError(alreadyResolvedError)
                : ContinuationResult.FromAlreadyResolved(alreadyResolvedResult!);
        }

        if (previousSnapshot is not null)
        {
            var toolResultError = await PersistIncomingToolResultsAsync(
                previousSnapshot,
                normalized,
                ct);
            if (toolResultError is not null)
                return ContinuationResult.FromError(toolResultError);
        }

        return ContinuationResult.FromPrevious(previousSnapshot);
    }

    private async Task<SessionRegistrationResult> RegisterSessionAsync(
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        try
        {
            var responseSession = await responseSessionRegistrationPort.RegisterAsync(
                BuildResponseSessionRecord(normalized, callerScope, createdAt),
                ct);
            return new SessionRegistrationResult(responseSession, null);
        }
        catch (OperationCanceledException)
        {
            return new SessionRegistrationResult(
                null,
                new ResponsesCommandError(408, "request_timeout", "Request timed out."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var correlation = LogAndCorrelate(logger, ex, "session_registration", normalized.ResponseId);
            return new SessionRegistrationResult(
                null,
                new ResponsesCommandError(
                    500,
                    "session_registration_failed",
                    $"Failed to register response session. Correlation: {correlation}"));
        }
    }

    private async Task<ExecutionPlanResult> BuildExecutionPlanAsync(
        NormalizedResponsesRequest normalized,
        LlmSessionSnapshot? previousSnapshot,
        ResponsesCallerScope callerScope,
        ChatRouteAction routeAction,
        string bearerToken,
        LlmSessionRegistrationResult responseSession,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var toolProviderContext = BuildToolProviderContext(callerScope, normalized.ResponseId, bearerToken);
        var forwardToModel = routeAction.ForwardToModel;
        var effectiveToolProviders = toolProviders;
        var toolChoiceHintPlan = ResponsesToolChoiceHintPlan.Empty;
        if (forwardToModel is not null)
        {
            if (forwardToModel.ToolSetRef != null && !string.IsNullOrWhiteSpace(forwardToModel.ToolSetRef.Name))
            {
                var toolSet = toolSetRegistry.Resolve(forwardToModel.ToolSetRef);
                if (!toolSet.IsSuccess)
                {
                    var error = toolSet.Error!;
                    return ExecutionPlanResult.FromError(new ResponsesCommandError(
                        500,
                        error.Code,
                        error.Message));
                }

                effectiveToolProviders = [.. toolProviders, new ToolSetResponsesToolProvider(toolSet.Sources)];
            }

            toolChoiceHintPlan = ResponsesToolChoiceHints.Create(
                forwardToModel.ToolChoiceHint?.ToolName,
                forwardToModel.ToolChoiceHint?.PrefilledArguments);
        }

        var toolClassification = await ResponsesToolClassifier.ClassifyAsync(
            normalized.DeclaredTools,
            effectiveToolProviders,
            toolProviderContext,
            logger,
            ct);
        var routedModel = string.IsNullOrWhiteSpace(forwardToModel?.ModelName)
            ? normalized.Model
            : forwardToModel.ModelName.Trim();
        var (effectiveModel, resolvedRouteValue) = await ResolveModelRouteAsync(routedModel, bearerToken, ct);
        var llmRequest = BuildLlmRequest(
            normalized,
            previousSnapshot,
            callerScope,
            bearerToken,
            effectiveModel,
            resolvedRouteValue,
            toolClassification);

        return ExecutionPlanResult.FromPlan(new ResponsesCreateCommandPlan(
            normalized,
            responseSession,
            previousSnapshot,
            llmRequest,
            toolProviderContext.ToolContextMetadata,
            toolClassification,
            toolChoiceHintPlan,
            createdAt));
    }

    private async Task<ResponsesCreateCommandResult> ExecuteNonStreamingAsync(
        ResponsesCreateCommandPlan plan,
        CancellationToken ct)
    {
        try
        {
            var provider = providerFactory.GetDefault();
            ResponsesCompletionResult completion;
            using (ResponsesToolContext.Push(plan.ToolChoiceHintPlan))
            {
                completion = await completionService.CollectAsync(
                    provider,
                    plan.LlmRequest,
                    plan.ToolContextMetadata,
                    plan.ToolClassification,
                    ct);
            }
            var forwardedToolCalls = completion.ForwardedToolCalls;
            await PersistForwardedToolCallsAsync(plan.Session, plan.ToolClassification, forwardedToolCalls, DateTimeOffset.UtcNow, ct);
            await TryResolveIncomingToolResultsAsync(plan.PreviousSnapshot, plan.Normalized, ct);
            var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Completed, ct);
            return ResponsesCreateCommandResult.FromCompleted(new ResponsesCreateCompletedCommandResult(
                plan.Normalized,
                plan.CreatedAt.ToUnixTimeSeconds(),
                completedAt,
                completion.Text,
                forwardedToolCalls,
                completion.Usage));
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesCreateCommandResult.FromError(401, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            var statusCode = ex.Status switch
            {
                401 or 403 => 401,
                429 => 429,
                503 => 503,
                >= 500 => 502,
                400 or 404 or 409 or 422 => ex.Status.Value,
                _ => 502,
            };

            var correlation = LogAndCorrelate(logger, ex, "nyxid_upstream", plan.Normalized.ResponseId);
            return ResponsesCreateCommandResult.FromError(
                statusCode,
                ex.Kind.ToString().ToLowerInvariant(),
                $"Upstream provider error. Correlation: {correlation}");
        }
        catch (OperationCanceledException)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return ResponsesCreateCommandResult.FromError(408, "request_timeout", "Request timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            var correlation = LogAndCorrelate(logger, ex, "execution", plan.Normalized.ResponseId);
            return ResponsesCreateCommandResult.FromError(
                500,
                "execution_failed",
                $"Execution failed. Correlation: {correlation}");
        }
    }

    private async Task<(string EffectiveModel, string? ResolvedRouteValue)> ResolveModelRouteAsync(
        string routedModel,
        string bearerToken,
        CancellationToken ct)
    {
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

        return (effectiveModel, resolvedRouteValue);
    }

    private static LLMRequest BuildLlmRequest(
        NormalizedResponsesRequest normalized,
        LlmSessionSnapshot? previousSnapshot,
        ResponsesCallerScope callerScope,
        string bearerToken,
        string effectiveModel,
        string? resolvedRouteValue,
        ResponsesToolClassification toolClassification)
    {
        var llmMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = normalized.ResponseId,
            [RegistrationScopeMetadataKey] = callerScope.ScopeId,
        };
        return new LLMRequest
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
            LlmControl = new LLMControlContext(
                NyxIdAccessToken: null,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                ModelOverride: null,
                NyxIdRoutePreference: resolvedRouteValue,
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: null),
            Model = effectiveModel,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxOutputTokens,
        };
    }

    private static ResponsesToolProviderContext BuildToolProviderContext(
        ResponsesCallerScope callerScope,
        string responseId,
        string bearerToken)
    {
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
                [RegistrationScopeMetadataKey] = callerScope.ScopeId,
                [LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken,
            });
    }

    private Task<ChatRouteDecision> ResolveResponsesChatRouteAsync(
        ResponsesCallerScope callerScope,
        string model,
        ToolMode toolMode,
        string contentHint,
        CancellationToken ct)
        => chatRouteDecisionPort.ResolveAsync(callerScope, model, toolMode, contentHint, ct);

    private static ToolMode ResolveToolMode(int declaredToolCount, int inlineToolResultCount)
    {
        if (inlineToolResultCount > 0)
            return ToolMode.Inline;
        return declaredToolCount > 0 ? ToolMode.Declared : ToolMode.None;
    }

    private static string BuildContentHint(string? content)
    {
        var normalized = content?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;
        const int maxContentHintLength = 160;
        return normalized.Length <= maxContentHintLength
            ? normalized
            : normalized[..maxContentHintLength];
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

    private static ResponsesCommandError? ValidatePreviousResponse(
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
            return new ResponsesCommandError(
                400,
                "previous_response_expired",
                "previous_response_id refers to an expired response session.");
        }

        if (visiblePrevious.Status is LlmSessionStatus.Cancelled
            or LlmSessionStatus.Expired
            or LlmSessionStatus.Failed)
        {
            return new ResponsesCommandError(
                400,
                "previous_response_not_available",
                "previous_response_id refers to a response session that cannot be continued.");
        }

        return null;
    }

    private static ResponsesCommandError? ValidateResponseVisibility(
        LlmSessionSnapshot? response,
        ResponsesCallerScope callerScope,
        string notFoundCode,
        string notFoundMessage)
    {
        if (response is null)
            return new ResponsesCommandError(404, notFoundCode, notFoundMessage);

        if (!string.Equals(response.ScopeId, callerScope.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(response.OwnerSubject, callerScope.OwnerSubject, StringComparison.Ordinal))
        {
            return new ResponsesCommandError(
                403,
                "response_scope_mismatch",
                "response id is not visible to the current caller scope.");
        }

        if (response.OriginKind != callerScope.OriginKind)
        {
            return new ResponsesCommandError(
                403,
                "response_origin_mismatch",
                "response id origin does not match the current ingress origin.");
        }

        return null;
    }

    private async Task<ResponsesCommandError?> PersistIncomingToolResultsAsync(
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
                return new ResponsesCommandError(
                    400,
                    "tool_call_not_found",
                    $"previous_response_id has no forwarded tool call '{result.CallId}'.");
            }

            var schemaHash = result.SchemaHash ?? call.SchemaHash;
            if (!string.Equals(call.SchemaHash, schemaHash, StringComparison.Ordinal))
            {
                return new ResponsesCommandError(
                    400,
                    "tool_schema_hash_mismatch",
                    $"Forwarded tool call '{result.CallId}' schema hash mismatch.");
            }

            if (call.Status == LlmSessionForwardedToolCallStatus.Resolved)
                continue;

            if (call.Status is LlmSessionForwardedToolCallStatus.Cancelled
                or LlmSessionForwardedToolCallStatus.Expired)
            {
                return new ResponsesCommandError(
                    400,
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
                return new ResponsesCommandError(400, "tool_result_rejected", ex.Message);
            }
        }

        return null;
    }

    private bool TryBuildAlreadyResolvedToolResultResponse(
        NormalizedResponsesRequest normalized,
        LlmSessionSnapshot previousSnapshot,
        out ResponsesCreateCompletedCommandResult? result,
        out ResponsesCommandError? error)
    {
        result = null;
        error = null;
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
                error = new ResponsesCommandError(
                    400,
                    "tool_schema_hash_mismatch",
                    $"Forwarded tool call '{input.CallId}' schema hash mismatch.");
                return true;
            }

            resolvedOutputs.Add(string.IsNullOrWhiteSpace(call.ResultJson) ? input.Output : call.ResultJson!);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var outputText = resolvedOutputs.Count == 1
            ? resolvedOutputs[0]
            : System.Text.Json.JsonSerializer.Serialize(resolvedOutputs);
        result = new ResponsesCreateCompletedCommandResult(normalized, now, now, outputText, [], null);
        return true;
    }

    private async Task TryResolveIncomingToolResultsAsync(
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

    private async Task PersistForwardedToolCallsAsync(
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

    private async Task TryUpdateSessionStatusAsync(
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
            logger.LogWarning(
                ex,
                "Failed to update response session {ResponseId} to {Status}.",
                responseSession.ResponseId,
                status);
        }
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

    private sealed record CallerScopeResult(
        ResponsesCallerScope? Scope,
        ResponsesCommandError? Error);

    private sealed record RouteTargetResult(
        ChatRouteAction? Action,
        ChatRouteAction? ForwardAction,
        ResponsesCommandError? Error)
    {
        public static RouteTargetResult FromModel(ChatRouteAction action) => new(action, null, null);

        public static RouteTargetResult FromForward(ChatRouteAction action) => new(null, action, null);

        public static RouteTargetResult FromError(int statusCode, string code, string message) =>
            new(null, null, new ResponsesCommandError(statusCode, code, message));
    }

    private sealed record ExecutionPlanResult(
        ResponsesCreateCommandPlan? Plan,
        ResponsesCommandError? Error)
    {
        public static ExecutionPlanResult FromPlan(ResponsesCreateCommandPlan plan) => new(plan, null);

        public static ExecutionPlanResult FromError(ResponsesCommandError error) => new(null, error);
    }

    private sealed record ContinuationResult(
        LlmSessionSnapshot? PreviousSnapshot,
        ResponsesCreateCompletedCommandResult? AlreadyResolved,
        ResponsesCommandError? Error)
    {
        public static ContinuationResult FromPrevious(LlmSessionSnapshot? previousSnapshot) =>
            new(previousSnapshot, null, null);

        public static ContinuationResult FromAlreadyResolved(ResponsesCreateCompletedCommandResult alreadyResolved) =>
            new(null, alreadyResolved, null);

        public static ContinuationResult FromError(ResponsesCommandError error) => new(null, null, error);
    }

    private sealed record SessionRegistrationResult(
        LlmSessionRegistrationResult? Session,
        ResponsesCommandError? Error);

    private sealed class ToolSetResponsesToolProvider : IResponsesToolProvider
    {
        private readonly IReadOnlyList<IAgentToolSource> _sources;

        public ToolSetResponsesToolProvider(IReadOnlyList<IAgentToolSource> sources)
        {
            _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        }

        public async ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default)
        {
            var tools = new List<IAgentTool>();
            foreach (var source in _sources)
                tools.AddRange(await source.DiscoverToolsAsync(ct));

            return tools;
        }
    }
}
