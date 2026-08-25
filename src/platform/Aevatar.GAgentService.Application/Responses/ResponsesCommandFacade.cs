using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.SkillInvocations;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Application.Internal;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Mainnet Host endpoints owned normalization, target resolution, session registration, tool persistence, and LLM command execution inline.
//   New principle: Application owns the Responses command lifecycle as a typed facade; Host maps HTTP/SSE/JSON frames around these command plans and results.
// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: ForwardToTeam/ForwardToGAgent skipped session lifecycle; Host new'd StringBuilder/Dictionary/List<ToolCall> to synthesize response.completed
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
// Refactor (iter81/cluster-081-direct-response-completion-not-session-fact):
//   Old pattern: direct Responses/Messages held terminal completion in request-local result; LlmSession only marked Completed
//   New principle: record typed LlmSessionCompletion on session for direct paths; terminal protocol output renders from session contract/readmodel
public sealed class ResponsesCommandFacade(
    IResponsesCallerScopeResolver callerScopeResolver,
    IResponsesChatRouteDecisionPort chatRouteDecisionPort,
    IResponsesRouteResolver routeResolver,
    ILlmSessionRegistrationPort responseSessionRegistrationPort,
    ILlmSessionQueryPort responseSessionQueryPort,
    IActorDispatchPort dispatchPort,
    IResponsesToolClassificationService toolClassificationService,
    IResponsesDirectToolPlanService directToolPlanService,
    ILlmSessionRunObservationService observationService,
    ILogger<ResponsesCommandFacade> logger,
    IOptions<ResponsesIngressOptions>? ingressOptions = null,
    IOwnerLlmConfigSource? ownerLlmConfigSource = null,
    ILlmRunExecutor? llmRunExecutor = null,
    IResponsesOwnedToolCatalogPlanner? ownedToolCatalogPlanner = null) : IResponsesCommandFacade
{
    private static readonly TimeSpan DefaultObservationTimeout = TimeSpan.FromSeconds(30);

    // Default model applied when a direct caller omits `model`; null preserves the
    // "model is required" contract (see ResponsesIngressOptions).
    private readonly string? _defaultIngressModel = ingressOptions?.Value?.NormalizedDefaultModel;

    // Configurable client-facing wait for a terminal event (raised from a hardcoded 30s so long
    // agentic turns are not cut). See ResponsesIngressOptions.ObservationTimeout.
    private readonly TimeSpan _observationTimeout =
        ingressOptions?.Value?.ObservationTimeout ?? DefaultObservationTimeout;
    private readonly bool _offActorLlmRunExecutorEnabled =
        ingressOptions?.Value?.OffActorLlmRunExecutorEnabled == true && llmRunExecutor is not null;

    public async Task<ResponsesCreateCommandResult> CreateAsync(
        ResponsesCommandRequest request,
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerScopeContext);

        var normalizedResult = ResponsesRequestNormalizer.Normalize(request, _defaultIngressModel);
        if (!normalizedResult.Succeeded)
        {
            return ResponsesCreateCommandResult.FromError(
                400,
                normalizedResult.ErrorCode ?? "invalid_request_error",
                normalizedResult.ErrorMessage ?? "Invalid request.");
        }

        var normalized = normalizedResult.Request!;
        var callerScopeResult = await ResolveCallerScopeAsync(callerScopeContext, ct);
        if (callerScopeResult.Error is not null)
            return ResponsesCreateCommandResult.FromError(
                callerScopeResult.Error.StatusCode,
                callerScopeResult.Error.Code,
                callerScopeResult.Error.Message);

        var callerScope = callerScopeResult.Scope!;
        // Single preferred-model source: explicit caller model > account UserConfig > route
        // policy / deployment default. See IngressModelPreference.
        var explicitCallerModel = IngressModelPreference.Normalize(request.Model);
        var ownerControl = await TryLoadOwnerControlAsync(callerScope.ScopeId, ct);
        var trigger = ParseSkillInvocationTrigger(normalized.Prompt);
        var routedModelResult = await ResolveRouteTargetAsync(
            normalized, callerScope, explicitCallerModel, ownerControl, trigger, ct);
        if (routedModelResult.Error is not null)
            return ResponsesCreateCommandResult.FromError(
                routedModelResult.Error.StatusCode,
                routedModelResult.Error.Code,
                routedModelResult.Error.Message);
        var createdAt = DateTimeOffset.UtcNow;
        var continuation = await PrepareContinuationAsync(normalized, callerScope, createdAt, ct);
        if (continuation.Error is not null)
            return ResponsesCreateCommandResult.FromError(
                continuation.Error.StatusCode,
                continuation.Error.Code,
                continuation.Error.Message);
        var sessionResult = await RegisterSessionAsync(normalized, callerScope, createdAt, ct);
        if (sessionResult.Error is not null)
            return ResponsesCreateCommandResult.FromError(
                sessionResult.Error.StatusCode,
                sessionResult.Error.Code,
                sessionResult.Error.Message);
        if (continuation.AlreadyResolvedCompletion is not null)
        {
            var completionResult = await RecordCompletionAsync(
                sessionResult.Session!,
                continuation.AlreadyResolvedCompletion,
                ct);
            return completionResult.Error is not null
                ? ResponsesCreateCommandResult.FromError(
                    completionResult.Error.StatusCode,
                    completionResult.Error.Code,
                    completionResult.Error.Message)
                : ResponsesCreateCommandResult.FromCompleted(new ResponsesCreateCompletedCommandResult(
                    normalized,
                    createdAt.ToUnixTimeSeconds(),
                    completionResult.Stage,
                    completionResult.Completion!));
        }

        var prepared = await BuildExecutionPlanAsync(
            normalized,
            continuation.PreviousSnapshot,
            callerScope,
            routedModelResult.Action!,
            ownerControl,
            trigger,
            callerScopeContext.InboundBearerToken,
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
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseId);
        ArgumentNullException.ThrowIfNull(callerScopeContext);

        var callerScopeResult = await ResolveCallerScopeAsync(callerScopeContext, ct);
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
                await responseSessionRegistrationPort.CancelRunAsync(
                    visibleSnapshot.ActorId,
                    visibleSnapshot.ResponseId,
                    $"{visibleSnapshot.ResponseId}:llm-run",
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
            var observed = await observationService.ObserveAsync(
                new LlmSessionRunObservationRequest(
                    plan.Session.ActorId,
                    plan.Session.ResponseId,
                    $"{plan.Session.ResponseId}:llm-run",
                    async token =>
                    {
                        var admission = _offActorLlmRunExecutorEnabled
                            ? await StartOffActorRunAsync(plan, token).ConfigureAwait(false)
                            : await DispatchRunAsync(plan, token).ConfigureAwait(false);
                        await TryResolveIncomingToolResultsAsync(plan.PreviousSnapshot, plan.Normalized, token)
                            .ConfigureAwait(false);
                        return admission;
                    },
                    _observationTimeout),
                async (delta, token) =>
                {
                    if (!string.IsNullOrEmpty(delta.TextDelta))
                        await onTextDelta(delta.TextDelta, token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
            if (observed.Error is not null)
            {
                return ResponsesStreamCommandResult.FromError(
                    observed.Error.StatusCode,
                    observed.Error.Code,
                    observed.Error.Message);
            }

            return observed.Completion is not null
                ? ResponsesStreamCommandResult.FromCompleted(observed.Completion)
                : ResponsesStreamCommandResult.FromError(
                    503,
                    "observation_unavailable",
                    "LLM run observation ended without a terminal event.");
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
            return ResponsesStreamCommandResult.FromError(408, "request_timeout", "Request timed out.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Streaming /v1/responses {ResponseId} failed", plan.Normalized.ResponseId);
            return ResponsesStreamCommandResult.FromError(500, "api_error", "Internal server error.");
        }
    }

    private async Task<CallerScopeResult> ResolveCallerScopeAsync(
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct)
    {
        try
        {
            var callerScope = await callerScopeResolver.ResolveAsync(callerScopeContext, ct);
            return new CallerScopeResult(callerScope, null);
        }
        catch (ResponsesCallerScopeUnavailableException ex)
        {
            return new CallerScopeResult(null, new ResponsesCommandError(401, "authentication_required", ex.Message));
        }
    }

    // Account UserConfig is the single "preferred model" source for ingress. Reads are
    // swallow-and-logged (mirroring OwnerLlmConfigApplier) so a flaky projection never fails a
    // request — resolution then falls through to the route policy / deployment default.
    private async Task<LLMControlContext?> TryLoadOwnerControlAsync(string scopeId, CancellationToken ct)
    {
        if (ownerLlmConfigSource is null || string.IsNullOrWhiteSpace(scopeId))
            return null;

        try
        {
            var config = await ownerLlmConfigSource.GetForScopeAsync(scopeId, ct).ConfigureAwait(false);
            return config.ApplyTo(LLMControlContext.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LLMSelectionRepairRequiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load owner LLM config for scope {ScopeId}; using request/deployment default model", scopeId);
            return null;
        }
    }

    private async Task<RouteTargetResult> ResolveRouteTargetAsync(
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope,
        string? explicitCallerModel,
        LLMControlContext? ownerControl,
        SkillInvocationTrigger? trigger,
        CancellationToken ct)
    {
        var routeDecision = await ResolveResponsesChatRouteAsync(
            callerScope,
            normalized.Model,
            ResolveToolMode(normalized.DeclaredTools.Count, normalized.ToolResults.Count),
            BuildContentHint(normalized.Prompt),
            trigger?.Name ?? string.Empty,
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

        var action = routeDecision.Action.Clone();
        // Preferred-model precedence: explicit caller > account UserConfig > route-policy
        // ForwardToModel (gated by ShouldUseRouteModel) > deployment default. The route policy
        // keeps Reject (above) and its tool-set selection; it can no longer silently swap an
        // explicit/preferred model.
        var routePolicyForwardModel = ShouldUseRouteModel(routeDecision, normalized.Model)
            ? action.ForwardToModel?.ModelName
            : null;
        var routedModel = IngressModelPreference.ResolveModel(
            explicitCallerModel,
            ownerControl?.ModelOverride,
            routePolicyForwardModel,
            normalized.Model);
        if (action.ForwardToModel is null)
        {
            action.ForwardToModel = new ForwardToModel();
        }

        action.ForwardToModel.ModelName = routedModel;
        return RouteTargetResult.FromModel(action);
    }

    private static bool ShouldUseRouteModel(ChatRouteDecision routeDecision, string requestModel)
    {
        var routeModel = routeDecision.Action.ForwardToModel?.ModelName;
        if (string.IsNullOrWhiteSpace(routeModel))
            return false;

        if (!routeDecision.UsedFallback)
        {
            return !string.IsNullOrWhiteSpace(routeDecision.MatchedRuleId) ||
                   ResponsesModelRouteParser.Parse(requestModel).RouteSlug is null;
        }

        return ResponsesModelRouteParser.Parse(requestModel).RouteSlug is null;
    }

    private async Task<ContinuationResult> PrepareContinuationAsync(
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt,
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
            TryBuildAlreadyResolvedToolResultCompletion(
                normalized,
                previousSnapshot,
                createdAt,
                out var alreadyResolvedCompletion,
                out var alreadyResolvedError))
        {
            return alreadyResolvedError is not null
                ? ContinuationResult.FromError(alreadyResolvedError)
                : ContinuationResult.FromAlreadyResolved(alreadyResolvedCompletion!);
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
        LLMControlContext? ownerControl,
        SkillInvocationTrigger? trigger,
        string bearerToken,
        LlmSessionRegistrationResult responseSession,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var toolProviderContext = BuildToolProviderContext(callerScope, normalized.ResponseId, bearerToken);
        var forwardToModel = routeAction.ForwardToModel;
        var toolPlan = directToolPlanService.Build(routeAction);
        if (toolPlan.Error is not null)
            return ExecutionPlanResult.FromError(toolPlan.Error);

        var ownedCatalogPlan = ownedToolCatalogPlanner is null
            ? new ResponsesOwnedToolCatalogPlan(
                Aevatar.AI.Core.AgentProfiles.AgentTurnToolCatalogFactory.RestrictedEmpty(),
                null,
                string.Empty,
                null)
            : await ownedToolCatalogPlanner.PlanAsync(
                routeAction,
                callerScope.ScopeId,
                normalized.ResponseId,
                normalized.Prompt ?? string.Empty,
                toolProviderContext.ToolContext,
                ct).ConfigureAwait(false);
        if (ownedCatalogPlan.Error is not null)
            return ExecutionPlanResult.FromError(ownedCatalogPlan.Error);

        var toolClassification = await toolClassificationService.ClassifyAsync(
            normalized.DeclaredTools,
            toolProviderContext,
            toolPlan.AdditionalToolProviders,
            ownedCatalogPlan.Catalog,
            ct);
        var routedModel = string.IsNullOrWhiteSpace(forwardToModel?.ModelName)
            ? normalized.Model
            : forwardToModel.ModelName.Trim();
        string effectiveModel;
        string? resolvedRoutePreference;
        LLMRouteTarget? resolvedRouteTarget;
        try
        {
            (effectiveModel, resolvedRoutePreference, resolvedRouteTarget) = await ResolveModelRouteAsync(
                routedModel,
                ownerControl,
                callerScope,
                ct);
        }
        catch (ResponsesModelNotFoundException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSession,
                LlmSessionStatus.Failed,
                CancellationToken.None);
            logger.LogInformation(ex, "Requested model route is not configured for response {ResponseId}", normalized.ResponseId);
            return ExecutionPlanResult.FromError(new ResponsesCommandError(
                404,
                "model_not_found",
                ex.Message));
        }
        catch (ResponsesRouteUnavailableException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSession,
                LlmSessionStatus.Failed,
                CancellationToken.None);
            logger.LogWarning(ex, "Model route inventory is unavailable for response {ResponseId}", normalized.ResponseId);
            return ExecutionPlanResult.FromError(new ResponsesCommandError(
                503,
                "model_route_unavailable",
                "The model routing inventory is temporarily unavailable."));
        }
        var toolContext = toolProviderContext.ToolContext with
        {
            Routing = toolProviderContext.ToolContext.Routing with
            {
                NyxIdRoutePreference = resolvedRoutePreference,
                RouteTarget = resolvedRouteTarget?.Clone(),
            },
            SkillRecovery = AgentSkillRecoveryContextBuilder.FromTrigger(trigger),
        };
        var llmRequest = BuildLlmRequest(
            normalized,
            previousSnapshot,
            callerScope,
            bearerToken,
            effectiveModel,
            resolvedRoutePreference,
            resolvedRouteTarget,
            toolClassification,
            toolContext);

        return ExecutionPlanResult.FromPlan(new ResponsesCreateCommandPlan(
            normalized,
            responseSession,
            previousSnapshot,
            llmRequest,
            toolContext,
            toolClassification,
            toolPlan.ToolChoiceHintPlan,
            createdAt,
            ownedCatalogPlan.ResolvedToolSetName,
            ownedCatalogPlan.ProfileSnapshot));
    }

    private async Task<ResponsesCreateCommandResult> ExecuteNonStreamingAsync(
        ResponsesCreateCommandPlan plan,
        CancellationToken ct)
    {
        try
        {
            var observed = await observationService.ObserveAsync(
                new LlmSessionRunObservationRequest(
                    plan.Session.ActorId,
                    plan.Session.ResponseId,
                    $"{plan.Session.ResponseId}:llm-run",
                    async token =>
                    {
                        var admission = _offActorLlmRunExecutorEnabled
                            ? await StartOffActorRunAsync(plan, token).ConfigureAwait(false)
                            : await DispatchRunAsync(plan, token).ConfigureAwait(false);
                        await TryResolveIncomingToolResultsAsync(plan.PreviousSnapshot, plan.Normalized, token)
                            .ConfigureAwait(false);
                        return admission;
                    },
                    _observationTimeout),
                null,
                ct).ConfigureAwait(false);
            if (observed.Error is not null)
            {
                return ResponsesCreateCommandResult.FromError(
                    observed.Error.StatusCode,
                    observed.Error.Code,
                    observed.Error.Message);
            }

            return observed.Completion is not null
                ? ResponsesCreateCommandResult.FromCompleted(new ResponsesCreateCompletedCommandResult(
                    plan.Normalized,
                    plan.CreatedAt.ToUnixTimeSeconds(),
                    ResponsesCompletionStage.ReadModelObserved,
                    observed.Completion))
                : ResponsesCreateCommandResult.FromError(
                    503,
                    "observation_unavailable",
                    "LLM run observation ended without a terminal event.");
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

    private async Task<(
        string EffectiveModel,
        string? ResolvedRoutePreference,
        LLMRouteTarget? RouteTarget)> ResolveModelRouteAsync(
        string routedModel,
        LLMControlContext? ownerControl,
        ResponsesCallerScope callerScope,
        CancellationToken ct)
    {
        var modelRoute = ResponsesModelRouteParser.Parse(routedModel);
        var effectiveModel = routedModel;
        string? resolvedRoutePreference = null;
        LLMRouteTarget? resolvedRouteTarget = null;
        if (modelRoute.RouteSlug is not null)
        {
            resolvedRouteTarget = await routeResolver
                .ResolveRouteTargetAsync(modelRoute.RouteSlug, modelRoute.Model, callerScope, ct)
                .ConfigureAwait(false);
            if (resolvedRouteTarget is null)
            {
                throw new ResponsesModelNotFoundException(
                    $"Model '{modelRoute.Model}' is not configured for service '{modelRoute.RouteSlug}'.");
            }

            effectiveModel = modelRoute.Model;
        }
        else
        {
            // Bare model (no vendor slug): honor the account's PreferredLlmRoute so the account
            // preference is fully expressed even when its model carries no slug.
            resolvedRouteTarget = ownerControl?.RouteTarget?.Clone();
            if (resolvedRouteTarget is null)
                resolvedRoutePreference = IngressModelPreference.Normalize(ownerControl?.NyxIdRoutePreference);
        }

        return (effectiveModel, resolvedRoutePreference, resolvedRouteTarget);
    }

    private static LLMRequest BuildLlmRequest(
        NormalizedResponsesRequest normalized,
        LlmSessionSnapshot? previousSnapshot,
        ResponsesCallerScope callerScope,
        string bearerToken,
        string effectiveModel,
        string? resolvedRoutePreference,
        LLMRouteTarget? resolvedRouteTarget,
        ResponsesToolClassification toolClassification,
        AgentToolExecutionContext toolContext)
    {
        return new LLMRequest
        {
            Messages = BuildLlmMessages(normalized, previousSnapshot),
            RequestId = normalized.ResponseId,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
            CallerContext = new LLMRequestCallerContext(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                normalized.ResponseId,
                new LLMRequestCallerCredentials(bearerToken)),
            Tools = toolClassification.EffectiveTools,
            ToolCatalogProof = toolClassification.OwnedCatalog?.Proof,
            ToolContext = toolContext,
            LlmControl = new LLMControlContext(
                NyxIdAccessToken: null,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                ModelOverride: null,
                NyxIdRoutePreference: resolvedRoutePreference,
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: null),
            RouteTarget = resolvedRouteTarget?.Clone(),
            Model = effectiveModel,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxOutputTokens,
        };
    }

    // Refactor (issue1416-first):
    //   Old pattern: Responses downgraded request identity, caller scope, and bearer credentials into ToolContextMetadata.
    //   New principle: Responses reuses AgentToolExecutionContext as the typed tool execution authority.
    private static ResponsesToolProviderContext BuildToolProviderContext(
        ResponsesCallerScope callerScope,
        string responseId,
        string bearerToken) =>
        new(BuildToolContext(callerScope, responseId, bearerToken, routePreference: null));

    private static AgentToolExecutionContext BuildToolContext(
        ResponsesCallerScope callerScope,
        string responseId,
        string bearerToken,
        string? routePreference) =>
        new(
            new AgentToolRequestIdentity(responseId, null),
            new AgentToolCredentials(bearerToken, null, null),
            new AgentToolCallerContext(callerScope.ScopeId, callerScope.OwnerSubject, responseId, callerScope.OwnerSubject),
            new AgentToolChannelContext(
                callerScope.OriginKind.ToString(),
                null,
                callerScope.ScopeId,
                null,
                null),
            AgentToolSenderBindingContext.Empty,
            new LLMRequestRoutingContext(null, routePreference, null, null),
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal));

    private Task<ChatRouteDecision> ResolveResponsesChatRouteAsync(
        ResponsesCallerScope callerScope,
        string model,
        ToolMode toolMode,
        string contentHint,
        string commandName,
        CancellationToken ct)
        => chatRouteDecisionPort.ResolveAsync(
            new ResponsesChatRouteDecisionRequest(
                callerScope,
                model,
                toolMode,
                contentHint,
                NormalizeRouteCommandName(commandName)),
            ct);

    private static SkillInvocationTrigger? ParseSkillInvocationTrigger(string? text) =>
        SkillInvocationTriggerParser.TryParse(text, platform: "cli", out var trigger)
            ? trigger
            : null;

    private static string NormalizeRouteCommandName(string? commandName) =>
        string.IsNullOrWhiteSpace(commandName)
            ? string.Empty
            : commandName.Trim().TrimStart('/').ToLowerInvariant();

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

    private bool TryBuildAlreadyResolvedToolResultCompletion(
        NormalizedResponsesRequest normalized,
        LlmSessionSnapshot previousSnapshot,
        DateTimeOffset completedAt,
        out LlmSessionCompletion? completion,
        out ResponsesCommandError? error)
    {
        completion = null;
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

        var outputText = resolvedOutputs.Count == 1
            ? resolvedOutputs[0]
            : System.Text.Json.JsonSerializer.Serialize(resolvedOutputs);
        completion = BuildSessionCompletion(outputText, [], null, completedAt);
        return true;
    }

    private async Task<CompletionRecordResult> RecordCompletionAsync(
        LlmSessionRegistrationResult session,
        LlmSessionCompletion completion,
        CancellationToken ct)
    {
        try
        {
            await responseSessionRegistrationPort.RecordCompletionAsync(
                session.ActorId,
                session.ResponseId,
                completion,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var correlation = LogAndCorrelate(logger, ex, "session_completion", session.ResponseId);
            return CompletionRecordResult.FromError(new ResponsesCommandError(
                500,
                "response_completion_record_failed",
                $"Failed to record response completion. Correlation: {correlation}"));
        }

        return CompletionRecordResult.FromCompletion(
            ResponsesCompletionStage.Committed,
            ToCompletionSnapshot(completion));
    }

    // Refactor (iter103/cluster-1 r2):
    //   Old pattern: dispatch was followed by an immediate readmodel completion read, upgrading accepted ACK into observed completion.
    //   New principle: this method returns only DispatchAdmission; completion must arrive through an explicit observation/readmodel path.
    private Task<DispatchAdmission> DispatchRunAsync(
        ResponsesCreateCommandPlan plan,
        CancellationToken ct)
    {
        var command = BuildRunRequested(
            plan.Session.ResponseId,
            plan.LlmRequest,
            plan.ToolClassification,
            plan.ToolChoiceHintPlan,
            plan.CreatedAt,
            plan.ResolvedToolSetName,
            plan.ProfileSnapshot);
        var envelope = ServiceCommandEnvelopeFactory.Create(
            plan.Session.ActorId,
            command,
            plan.Session.ResponseId);
        return dispatchPort.DispatchAsync(plan.Session.ActorId, envelope, ct);
    }

    private async Task<DispatchAdmission> StartOffActorRunAsync(
        ResponsesCreateCommandPlan plan,
        CancellationToken ct)
    {
        var request = BuildExecutorRequest(plan);
        return await llmRunExecutor!.StartAsync(request, ct).ConfigureAwait(false);
    }

    private static LlmRunExecutorRequest BuildExecutorRequest(ResponsesCreateCommandPlan plan)
    {
        var command = BuildRunRequested(
            plan.Session.ResponseId,
            plan.LlmRequest,
            plan.ToolClassification,
            plan.ToolChoiceHintPlan,
            plan.CreatedAt,
            plan.ResolvedToolSetName,
            plan.ProfileSnapshot);
        return new LlmRunExecutorRequest(
            plan.Session.ActorId,
            plan.Session.ResponseId,
            command.RunId,
            command,
            plan.ToolContext.Channel.Platform);
    }

    private static LlmRunRequested BuildRunRequested(
        string responseId,
        LLMRequest request,
        ResponsesToolClassification toolClassification,
        ResponsesToolChoiceHintPlan toolChoiceHintPlan,
        DateTimeOffset requestedAt,
        string toolSetName,
        AgentProfileSnapshot? profileSnapshot)
    {
        var command = new LlmRunRequested
        {
            ResponseId = responseId,
            RunId = $"{responseId}:llm-run",
            Model = request.Model ?? string.Empty,
            RoutePreference = request.LlmControl?.NyxIdRoutePreference ?? string.Empty,
            ScopeId = request.CallerContext?.ScopeId ?? string.Empty,
            OwnerSubject = request.CallerContext?.OwnerSubject ?? string.Empty,
            BearerToken = request.CallerContext?.Credentials?.NyxIdBearer ?? string.Empty,
            ToolContext = (request.ToolContext ?? AgentToolExecutionContext.Empty).ToPayload(),
            RequestedAt = Timestamp.FromDateTimeOffset(requestedAt),
        };
        if (request.Temperature is not null)
            command.Temperature = request.Temperature.Value;
        if (request.MaxTokens is not null)
            command.MaxTokens = request.MaxTokens.Value;
        if (request.RouteTarget is not null)
            command.RouteTarget = request.RouteTarget.Clone();
        command.Messages.AddRange(request.Messages.Select(ToRuntimeMessage));
        command.ToolSelection = ResponsesRuntimeToolSelectionFactory.Create(
            toolClassification, toolChoiceHintPlan, toolSetName, profileSnapshot);
        return command;
    }

    private static LlmSessionRuntimeChatMessage ToRuntimeMessage(ChatMessage message)
    {
        var result = new LlmSessionRuntimeChatMessage
        {
            Role = message.Role,
            Content = message.Content ?? string.Empty,
            ReasoningContent = message.ReasoningContent ?? string.Empty,
            ToolCallId = message.ToolCallId ?? string.Empty,
        };
        if (message.ToolCalls is { Count: > 0 })
            result.ToolCalls.AddRange(message.ToolCalls.Select(ToRuntimeToolCall));
        return result;
    }

    private static LlmSessionRuntimeToolCall ToRuntimeToolCall(ToolCall call) =>
        new()
        {
            CallId = call.Id,
            ToolName = call.Name,
            ArgumentsJson = call.ArgumentsJson,
            Arguments = ResponsesProtoPayloads.ParseStruct(call.ArgumentsJson),
        };

    private static LlmSessionCompletion BuildSessionCompletion(
        string outputText,
        IReadOnlyList<ToolCall> forwardedToolCalls,
        TokenUsage? usage,
        DateTimeOffset completedAt)
    {
        var completion = new LlmSessionCompletion
        {
            OutputText = outputText,
            CompletedAt = Timestamp.FromDateTimeOffset(completedAt),
        };

        if (usage is not null)
        {
            completion.Usage = new LlmSessionTokenUsage
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
            };
        }

        foreach (var toolCall in forwardedToolCalls)
        {
            completion.ToolCalls.Add(new LlmSessionCompletedToolCall
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                Result = ResponsesJsonValues.ParseBoundaryPayload(
                    string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson),
            });
        }

        return completion;
    }

    private static LlmSessionCompletionSnapshot ToCompletionSnapshot(LlmSessionCompletion completion) =>
        new(
            completion.OutputText,
            completion.ToolCalls
                .Select(static tool => new LlmSessionCompletedToolCallSnapshot(
                    tool.CallId,
                    tool.ToolName,
                    ResponsesJsonValues.ToBoundaryJson(tool.Result)))
                .ToArray(),
            completion.CompletedAt?.ToDateTimeOffset(),
            string.IsNullOrWhiteSpace(completion.FailureCode) ? null : completion.FailureCode,
            string.IsNullOrWhiteSpace(completion.FailureMessage) ? null : completion.FailureMessage,
            completion.Usage is null
                ? null
                : new TokenUsage(
                    completion.Usage.PromptTokens,
                    completion.Usage.CompletionTokens,
                    completion.Usage.TotalTokens));

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
        ResponsesCommandError? Error)
    {
        public static RouteTargetResult FromModel(ChatRouteAction action) => new(action, null);

        public static RouteTargetResult FromError(int statusCode, string code, string message) =>
            new(null, new ResponsesCommandError(statusCode, code, message));
    }

    private sealed record ContinuationResult(
        LlmSessionSnapshot? PreviousSnapshot,
        LlmSessionCompletion? AlreadyResolvedCompletion,
        ResponsesCommandError? Error)
    {
        public static ContinuationResult FromPrevious(LlmSessionSnapshot? previousSnapshot) =>
            new(previousSnapshot, null, null);

        public static ContinuationResult FromAlreadyResolved(LlmSessionCompletion alreadyResolved) =>
            new(null, alreadyResolved, null);

        public static ContinuationResult FromError(ResponsesCommandError error) => new(null, null, error);
    }

    private sealed record SessionRegistrationResult(
        LlmSessionRegistrationResult? Session,
        ResponsesCommandError? Error);

    private sealed record ExecutionPlanResult(
        ResponsesCreateCommandPlan? Plan,
        ResponsesCommandError? Error)
    {
        public static ExecutionPlanResult FromPlan(ResponsesCreateCommandPlan plan) => new(plan, null);

        public static ExecutionPlanResult FromError(ResponsesCommandError error) => new(null, error);
    }

    private sealed record CompletionRecordResult(
        ResponsesCommandError? Error,
        ResponsesCompletionStage Stage,
        LlmSessionCompletionSnapshot? Completion)
    {
        public static CompletionRecordResult FromError(ResponsesCommandError error) =>
            new(error, ResponsesCompletionStage.Committed, null);

        public static CompletionRecordResult FromCompletion(
            ResponsesCompletionStage stage,
            LlmSessionCompletionSnapshot completion) =>
            new(null, stage, completion);
    }
}
