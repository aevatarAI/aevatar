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
//   Old pattern: Anthropic Messages Host handler normalized, resolved chat route, registered sessions, classified tools, and built LLM requests inline.
//   New principle: Application owns the Messages command lifecycle as a typed facade; Host only maps Anthropic HTTP/SSE/JSON frames.
// Refactor (iter81/cluster-081-direct-response-completion-not-session-fact):
//   Old pattern: direct Responses/Messages held terminal completion in request-local result; LlmSession only marked Completed
//   New principle: record typed LlmSessionCompletion on session for direct paths; terminal protocol output renders from session contract/readmodel
public sealed class MessagesCommandFacade(
    IResponsesCallerScopeResolver callerScopeResolver,
    IResponsesChatRouteDecisionPort chatRouteDecisionPort,
    IResponsesRouteResolver routeResolver,
    ILlmSessionRegistrationPort sessionRegistrationPort,
    IActorDispatchPort dispatchPort,
    IResponsesToolClassificationService toolClassificationService,
    IResponsesDirectToolPlanService directToolPlanService,
    ILlmSessionRunObservationService observationService,
    ILogger<MessagesCommandFacade> logger,
    IOptions<ResponsesIngressOptions>? ingressOptions = null,
    IOwnerLlmConfigSource? ownerLlmConfigSource = null,
    ILlmRunExecutor? llmRunExecutor = null) : IMessagesCommandFacade
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

    public async Task<MessagesCreateCommandResult> CreateAsync(
        MessagesCommandRequest request,
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerScopeContext);

        var normalizedResult = MessagesRequestNormalizer.Normalize(request, _defaultIngressModel);
        if (!normalizedResult.Succeeded)
        {
            return MessagesCreateCommandResult.FromError(
                400,
                normalizedResult.ErrorCode ?? "invalid_request_error",
                normalizedResult.ErrorMessage ?? "Invalid request.");
        }

        var normalized = normalizedResult.Request!;
        var callerScopeResult = await ResolveCallerScopeAsync(callerScopeContext, ct);
        if (callerScopeResult.Error is not null)
            return MessagesCreateCommandResult.FromError(
                callerScopeResult.Error.StatusCode,
                "authentication_error",
                callerScopeResult.Error.Message);

        // Single preferred-model source: explicit caller model > account UserConfig > route
        // policy / deployment default. See IngressModelPreference.
        var explicitCallerModel = IngressModelPreference.Normalize(request.Model);
        var ownerControl = await TryLoadOwnerControlAsync(callerScopeResult.Scope!.ScopeId, ct);

        var trigger = ParseSkillInvocationTrigger(BuildRouteContentHint(normalized));
        var routedModelResult = await ResolveRouteTargetAsync(
            normalized, callerScopeResult.Scope!, explicitCallerModel, ownerControl, trigger, ct);
        if (routedModelResult.Error is not null)
            return MessagesCreateCommandResult.FromError(
                routedModelResult.Error.StatusCode,
                routedModelResult.Error.Code,
                routedModelResult.Error.Message);

        var sessionResult = await RegisterSessionAsync(normalized, callerScopeResult.Scope!, DateTimeOffset.UtcNow, ct);
        if (sessionResult.Error is not null)
            return MessagesCreateCommandResult.FromError(
                sessionResult.Error.StatusCode,
                sessionResult.Error.Code,
                sessionResult.Error.Message);

        var planResult = await BuildExecutionPlanAsync(
            normalized,
            callerScopeResult.Scope!,
            routedModelResult.Model!,
            routedModelResult.Action!,
            ownerControl,
            trigger,
            callerScopeContext.InboundBearerToken,
            sessionResult.Session!,
            ct);

        if (planResult.Error is not null)
            return MessagesCreateCommandResult.FromError(
                planResult.Error.StatusCode,
                planResult.Error.Code,
                planResult.Error.Message);

        return normalized.Stream
            ? MessagesCreateCommandResult.FromStreamPlan(planResult.Plan!)
            : await ExecuteNonStreamingAsync(planResult.Plan!, ct);
    }

    public async Task<ResponsesStreamCommandResult> StreamAsync(
        MessagesCreateCommandPlan plan,
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
                    token => _offActorLlmRunExecutorEnabled
                        ? StartOffActorRunAsync(plan, token)
                        : DispatchRunAsync(plan, token),
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
            return ResponsesStreamCommandResult.FromError(401, "authentication_error", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(ex.Status ?? 502, ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ResponsesStreamCommandResult.FromError(499, "client_closed_request", "Client closed request.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Streaming /v1/messages {MessageId} failed", plan.Normalized.MessageId);
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
            return new CallerScopeResult(null, new ResponsesCommandError(401, "authentication_error", ex.Message));
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
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        string? explicitCallerModel,
        LLMControlContext? ownerControl,
        SkillInvocationTrigger? trigger,
        CancellationToken ct)
    {
        var routeDecision = await ResolveResponsesChatRouteAsync(
            callerScope,
            normalized.Model,
            ResolveToolMode(normalized.DeclaredTools.Count, inlineToolResultCount: 0),
            BuildRouteContentHint(normalized),
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
        return RouteTargetResult.FromModel(routedModel, action);
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

    private async Task<SessionRegistrationResult> RegisterSessionAsync(
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        try
        {
            var session = await sessionRegistrationPort.RegisterAsync(
                BuildSessionRecord(normalized, callerScope, createdAt),
                ct);
            return new SessionRegistrationResult(session, null);
        }
        catch (OperationCanceledException)
        {
            return new SessionRegistrationResult(null, new ResponsesCommandError(408, "request_timeout", "Request timed out."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to register llm session for message {MessageId}", normalized.MessageId);
            return new SessionRegistrationResult(null, new ResponsesCommandError(500, "api_error", "Failed to register session."));
        }
    }

    private async Task<ExecutionPlanResult> BuildExecutionPlanAsync(
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        string routedModel,
        ChatRouteAction routeAction,
        LLMControlContext? ownerControl,
        SkillInvocationTrigger? trigger,
        string bearerToken,
        LlmSessionRegistrationResult session,
        CancellationToken ct)
    {
        var toolProviderContext = BuildToolProviderContext(callerScope, normalized.MessageId, bearerToken);
        var toolPlan = directToolPlanService.Build(routeAction);
        if (toolPlan.Error is not null)
            return ExecutionPlanResult.FromError(toolPlan.Error);

        var toolClassification = await toolClassificationService.ClassifyAsync(
            normalized.DeclaredTools,
            toolProviderContext,
            toolPlan.AdditionalToolProviders,
            ct: ct);
        var (effectiveModel, resolvedRouteValue) = await ResolveModelRouteAsync(
            routedModel, ownerControl?.NyxIdRoutePreference, bearerToken, ct);
        var toolContext = toolProviderContext.ToolContext with
        {
            Routing = toolProviderContext.ToolContext.Routing with
            {
                NyxIdRoutePreference = resolvedRouteValue,
            },
            SkillRecovery = AgentSkillRecoveryContextBuilder.FromTrigger(trigger),
        };
        var llmRequest = BuildLlmRequest(
            normalized,
            callerScope,
            bearerToken,
            effectiveModel,
            resolvedRouteValue,
            toolClassification,
            toolContext);
        if (normalized.DroppedImageContent)
        {
            logger.LogWarning(
                "Image content blocks dropped from Messages request {MessageId}; Path B is text-only in v1.",
                normalized.MessageId);
        }

        return ExecutionPlanResult.FromPlan(new MessagesCreateCommandPlan(
            normalized,
            session,
            llmRequest,
            toolContext,
            toolClassification,
            toolPlan.ToolChoiceHintPlan,
            toolPlan.ResolvedToolSetName));
    }

    private async Task<MessagesCreateCommandResult> ExecuteNonStreamingAsync(
        MessagesCreateCommandPlan plan,
        CancellationToken ct)
    {
        try
        {
            var admission = _offActorLlmRunExecutorEnabled
                ? await StartOffActorRunAsync(plan, ct).ConfigureAwait(false)
                : await DispatchRunAsync(plan, ct).ConfigureAwait(false);
            return MessagesCreateCommandResult.FromAccepted(new MessagesCreateAcceptedCommandResult(
                plan.Normalized,
                plan.Session,
                admission));
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return MessagesCreateCommandResult.FromError(401, "authentication_error", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            var statusCode = ex.Status switch
            {
                400 => 400,
                401 => 401,
                403 => 403,
                404 => 404,
                429 => 429,
                >= 500 => 502,
                _ => 502,
            };
            return MessagesCreateCommandResult.FromError(statusCode, ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return MessagesCreateCommandResult.FromError(499, "client_closed_request", "Client closed request.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Unexpected error processing /v1/messages {MessageId}", plan.Normalized.MessageId);
            return MessagesCreateCommandResult.FromError(500, "api_error", "Internal server error.");
        }
    }

    private async Task<(string EffectiveModel, string? ResolvedRouteValue)> ResolveModelRouteAsync(
        string routedModel,
        string? accountPreferredRoute,
        string bearerToken,
        CancellationToken ct)
    {
        // Bare model + account PreferredLlmRoute → honor the account route (single preference
        // source) instead of defaulting to the anthropic vendor route.
        if (!routedModel.Contains('/', StringComparison.Ordinal)
            && IngressModelPreference.Normalize(accountPreferredRoute) is { } accountRoute)
        {
            return (routedModel, accountRoute);
        }

        var anthropicPrefixed = false;
        if (!routedModel.Contains('/', StringComparison.Ordinal))
        {
            routedModel = $"anthropic/{routedModel}";
            anthropicPrefixed = true;
        }

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
            else if (anthropicPrefixed)
                effectiveModel = modelRoute.Model;
        }

        return (effectiveModel, resolvedRouteValue);
    }

    private static LLMRequest BuildLlmRequest(
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        string bearerToken,
        string effectiveModel,
        string? resolvedRouteValue,
        ResponsesToolClassification toolClassification,
        AgentToolExecutionContext toolContext)
    {
        return new LLMRequest
        {
            Messages = [.. normalized.ChatMessages],
            RequestId = normalized.MessageId,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
            CallerContext = new LLMRequestCallerContext(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                normalized.MessageId,
                new LLMRequestCallerCredentials(bearerToken)),
            Tools = toolClassification.EffectiveTools,
            ToolContext = toolContext,
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
            MaxTokens = normalized.MaxTokens,
        };
    }

    // Refactor (issue1416-first):
    //   Old pattern: Messages downgraded request identity, caller scope, and bearer credentials into ToolContextMetadata.
    //   New principle: Messages reuses AgentToolExecutionContext as the typed tool execution authority.
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

    private static string BuildRouteContentHint(NormalizedMessagesRequest normalized) =>
        normalized.ChatMessages
            .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content
        ?? normalized.ChatMessages.LastOrDefault()?.Content
        ?? string.Empty;

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

    private async Task TryUpdateSessionStatusAsync(
        LlmSessionRegistrationResult session,
        LlmSessionStatus status,
        CancellationToken ct)
    {
        try
        {
            await sessionRegistrationPort.UpdateStatusAsync(session.ActorId, session.ResponseId, status, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update llm session {ResponseId} to {Status}", session.ResponseId, status);
        }
    }

    // Refactor (iter103/cluster-1 r2):
    //   Old pattern: dispatch was followed by an immediate readmodel completion read, upgrading accepted ACK into observed completion.
    //   New principle: this method returns only DispatchAdmission; completion must arrive through an explicit observation/readmodel path.
    private Task<DispatchAdmission> DispatchRunAsync(
        MessagesCreateCommandPlan plan,
        CancellationToken ct)
    {
        var command = BuildRunRequested(
            plan.Session.ResponseId,
            plan.LlmRequest,
            plan.ToolClassification,
            plan.ToolChoiceHintPlan,
            plan.ResolvedToolSetName);
        var envelope = ServiceCommandEnvelopeFactory.Create(
            plan.Session.ActorId,
            command,
            plan.Session.ResponseId);
        return dispatchPort.DispatchAsync(plan.Session.ActorId, envelope, ct);
    }

    private async Task<DispatchAdmission> StartOffActorRunAsync(
        MessagesCreateCommandPlan plan,
        CancellationToken ct)
    {
        var request = BuildExecutorRequest(plan);
        return await llmRunExecutor!.StartAsync(request, ct).ConfigureAwait(false);
    }

    private static LlmRunExecutorRequest BuildExecutorRequest(MessagesCreateCommandPlan plan)
    {
        var command = BuildRunRequested(
            plan.Session.ResponseId,
            plan.LlmRequest,
            plan.ToolClassification,
            plan.ToolChoiceHintPlan,
            plan.ResolvedToolSetName);
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
        string toolSetName)
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
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        if (request.Temperature is not null)
            command.Temperature = request.Temperature.Value;
        if (request.MaxTokens is not null)
            command.MaxTokens = request.MaxTokens.Value;
        command.Messages.AddRange(request.Messages.Select(ToRuntimeMessage));
        command.ToolSelection = ResponsesRuntimeToolSelectionFactory.Create(
            toolClassification, toolChoiceHintPlan, toolSetName);
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
            result.ToolCalls.AddRange(message.ToolCalls.Select(static call => new LlmSessionRuntimeToolCall
            {
                CallId = call.Id,
                ToolName = call.Name,
                ArgumentsJson = call.ArgumentsJson,
                Arguments = ResponsesProtoPayloads.ParseStruct(call.ArgumentsJson),
            }));
        return result;
    }

    // Refactor (iter355/issue1438-first):
    //   Old pattern: Messages LlmRunRequested persisted tool schemas and hints as JSON strings.
    //   New principle: typed Struct fields carry new writes; JSON strings remain legacy fallback.
    private sealed record CallerScopeResult(
        ResponsesCallerScope? Scope,
        ResponsesCommandError? Error);

    private sealed record RouteTargetResult(
        string? Model,
        ChatRouteAction? Action,
        ResponsesCommandError? Error)
    {
        public static RouteTargetResult FromModel(string model, ChatRouteAction action) => new(model, action, null);

        public static RouteTargetResult FromError(int statusCode, string code, string message) =>
            new(null, null, new ResponsesCommandError(statusCode, code, message));
    }

    private sealed record SessionRegistrationResult(
        LlmSessionRegistrationResult? Session,
        ResponsesCommandError? Error);

    private sealed record ExecutionPlanResult(
        MessagesCreateCommandPlan? Plan,
        ResponsesCommandError? Error)
    {
        public static ExecutionPlanResult FromPlan(MessagesCreateCommandPlan plan) => new(plan, null);

        public static ExecutionPlanResult FromError(ResponsesCommandError error) => new(null, error);
    }

}
