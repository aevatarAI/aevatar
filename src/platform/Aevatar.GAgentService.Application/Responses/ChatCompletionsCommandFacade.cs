using System.Security.Cryptography;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.SkillInvocations;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Application.Internal;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter344/cluster-001):
//   Old pattern: Host handler owns caller resolution, route resolution, session registration, tool planning, direct provider execution, status updates, and protocol rendering in one request stack.
//   New principle: Host maps HTTP/OpenAI frames only; typed Application facade owns Normalize -> Resolve Target -> Build Context -> Build Envelope -> Dispatch -> Receipt/Observe via the same LlmSessionGAgent run path as Responses/Messages.
public sealed class ChatCompletionsCommandFacade(
    IResponsesCallerScopeResolver callerScopeResolver,
    IResponsesChatRouteDecisionPort chatRouteDecisionPort,
    IResponsesRouteResolver routeResolver,
    ILlmSessionRegistrationPort sessionRegistrationPort,
    ILlmSessionObservationScopeLeasePreparationPort observationScopeLeasePreparationPort,
    ILlmSessionObservationProjectionPort observationProjectionPort,
    IActorDispatchPort dispatchPort,
    IResponsesToolClassificationService toolClassificationService,
    IResponsesDirectToolPlanService directToolPlanService,
    ILogger<ChatCompletionsCommandFacade> logger,
    TimeSpan? observationTimeout = null) : IChatCompletionsCommandFacade
{
    private static readonly TimeSpan DefaultObservationTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _observationTimeout = observationTimeout ?? DefaultObservationTimeout;
    public async Task<ChatCompletionsCreateCommandResult> CreateAsync(
        ChatCompletionsCommandRequest request,
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerScopeContext);

        var normalizedResult = Normalize(request);
        if (!normalizedResult.Succeeded)
        {
            return ChatCompletionsCreateCommandResult.FromError(
                400,
                normalizedResult.ErrorCode ?? "invalid_request_error",
                normalizedResult.ErrorMessage ?? "Invalid request.");
        }

        var normalized = normalizedResult.Request!;
        var callerScopeResult = await ResolveCallerScopeAsync(callerScopeContext, ct);
        if (callerScopeResult.Error is not null)
            return ChatCompletionsCreateCommandResult.FromError(
                callerScopeResult.Error.StatusCode,
                callerScopeResult.Error.Code,
                callerScopeResult.Error.Message);

        var trigger = ParseSkillInvocationTrigger(BuildRouteContentHint(normalized));
        var routedModelResult = await ResolveRouteTargetAsync(normalized, callerScopeResult.Scope!, trigger, ct);
        if (routedModelResult.Error is not null)
            return ChatCompletionsCreateCommandResult.FromError(
                routedModelResult.Error.StatusCode,
                routedModelResult.Error.Code,
                routedModelResult.Error.Message);

        var createdAt = DateTimeOffset.UtcNow;
        var sessionResult = await RegisterSessionAsync(normalized, callerScopeResult.Scope!, createdAt, ct);
        if (sessionResult.Error is not null)
            return ChatCompletionsCreateCommandResult.FromError(
                sessionResult.Error.StatusCode,
                sessionResult.Error.Code,
                sessionResult.Error.Message);

        var planResult = await BuildExecutionPlanAsync(
            normalized,
            callerScopeResult.Scope!,
            routedModelResult.Model!,
            routedModelResult.Action!,
            trigger,
            callerScopeContext.InboundBearerToken,
            sessionResult.Session!,
            createdAt,
            ct);
        if (planResult.Error is not null)
            return ChatCompletionsCreateCommandResult.FromError(
                planResult.Error.StatusCode,
                planResult.Error.Code,
                planResult.Error.Message);

        return normalized.Stream
            ? ChatCompletionsCreateCommandResult.FromStreamPlan(planResult.Plan!)
            : await ExecuteNonStreamingAsync(planResult.Plan!, ct);
    }

    public async Task<ResponsesStreamCommandResult> StreamAsync(
        ChatCompletionsCreateCommandPlan plan,
        Func<ChatCompletionsObservedDelta, CancellationToken, ValueTask> onObservedDelta,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(onObservedDelta);

        try
        {
            return ResponsesStreamCommandResult.FromCompleted(
                await ObserveCompletionAsync(plan, onObservedDelta, ct).ConfigureAwait(false));
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(401, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(ResolveUpstreamStatusCode(ex), ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (ObservedLlmRunCancelledException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(409, "run_cancelled", ex.Message);
        }
        catch (ObservedLlmRunFailedException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(500, "llm_run_failed", ex.Message);
        }
        catch (TimeoutException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogWarning(ex, "Streaming /v1/chat/completions {CompletionId} timed out waiting for a terminal observation event", plan.Normalized.CompletionId);
            return ResponsesStreamCommandResult.FromError(504, "response_timeout", ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(499, "client_closed_request", "Client closed request.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Streaming /v1/chat/completions {CompletionId} failed", plan.Normalized.CompletionId);
            return ResponsesStreamCommandResult.FromError(500, "api_error", "Internal server error.");
        }
    }

    // Refactor (iter344/cluster-001):
    //   Old pattern: /v1/chat/completions validated model and generation controls inside the Host request stack.
    //   New principle: Application owns command normalization before caller, routing, session, and dispatch work starts.
    private static ChatCompletionsRequestNormalizationResult Normalize(ChatCompletionsCommandRequest request)
    {
        var model = request.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return ChatCompletionsRequestNormalizationResult.Failed("model_required", "model is required.");

        if (request.MaxTokens is <= 0)
            return ChatCompletionsRequestNormalizationResult.Failed("invalid_max_tokens", "max_tokens must be greater than zero when provided.");

        if (request.Temperature is < 0 or > 2)
            return ChatCompletionsRequestNormalizationResult.Failed("invalid_temperature", "temperature must be between 0 and 2.");

        if (request.ChatMessages.Count == 0)
            return ChatCompletionsRequestNormalizationResult.Failed("invalid_messages", "messages must contain at least one entry.");

        return ChatCompletionsRequestNormalizationResult.Success(new NormalizedChatCompletionsCommand(
            "chatcmpl_" + NewOpaqueId(),
            model,
            request.Stream == true,
            request.IncludeUsageInStream,
            request.Temperature,
            request.MaxTokens,
            request.ChatMessages,
            request.DeclaredTools,
            request.ResponseFormat));
    }

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host converted inbound bearer evidence into caller scope while also preparing LLM execution.
    //   New principle: Caller-scope resolution is an Application command step with a typed protocol error boundary.
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

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host queried route policy and rewrote the model inline before opening a session.
    //   New principle: Application resolves the command target and returns either a routed model/action or a typed rejection.
    private async Task<RouteTargetResult> ResolveRouteTargetAsync(
        NormalizedChatCompletionsCommand normalized,
        ResponsesCallerScope callerScope,
        SkillInvocationTrigger? trigger,
        CancellationToken ct)
    {
        var routeDecision = await chatRouteDecisionPort.ResolveAsync(
            new ResponsesChatRouteDecisionRequest(
                callerScope,
                normalized.Model,
                normalized.DeclaredTools.Count > 0 ? ToolMode.Declared : ToolMode.None,
                BuildRouteContentHint(normalized),
                NormalizeRouteCommandName(trigger?.Name)),
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
        var routedModel = !string.IsNullOrWhiteSpace(action.ForwardToModel?.ModelName)
            ? action.ForwardToModel.ModelName.Trim()
            : normalized.Model;
        if (action.ForwardToModel is null)
            action.ForwardToModel = new ForwardToModel();

        action.ForwardToModel.ModelName = routedModel;
        return RouteTargetResult.FromModel(routedModel, action);
    }

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host registered Chat Completions sessions as part of direct request-local provider execution.
    //   New principle: Application registers the actor-owned session before dispatch and reports only the reached command stage.
    private async Task<SessionRegistrationResult> RegisterSessionAsync(
        NormalizedChatCompletionsCommand normalized,
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
            logger.LogError(ex, "Failed to register llm session for chat completion {CompletionId}", normalized.CompletionId);
            return new SessionRegistrationResult(null, new ResponsesCommandError(500, "api_error", "Failed to register session."));
        }
    }

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host assembled tool provider context, route preference, and LLM request just before direct provider calls.
    //   New principle: Application builds one dispatch plan that carries typed run input into LlmSessionGAgent.
    private async Task<ExecutionPlanResult> BuildExecutionPlanAsync(
        NormalizedChatCompletionsCommand normalized,
        ResponsesCallerScope callerScope,
        string routedModel,
        ChatRouteAction routeAction,
        SkillInvocationTrigger? trigger,
        string bearerToken,
        LlmSessionRegistrationResult session,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var toolProviderContext = BuildToolProviderContext(callerScope, normalized.CompletionId, bearerToken);
        var toolPlan = directToolPlanService.Build(routeAction);
        if (toolPlan.Error is not null)
            return ExecutionPlanResult.FromError(toolPlan.Error);

        var toolClassification = await toolClassificationService.ClassifyAsync(
            normalized.DeclaredTools,
            toolProviderContext,
            toolPlan.AdditionalToolProviders,
            ct: ct);
        var (effectiveModel, resolvedRouteValue) = await ResolveModelRouteAsync(routedModel, bearerToken, ct);
        var toolContext = toolProviderContext.ToolContext with
        {
            Routing = toolProviderContext.ToolContext.Routing with
            {
                NyxIdRoutePreference = resolvedRouteValue,
            },
            SkillRecovery = BuildSkillRecoveryContext(trigger),
        };
        var llmRequest = BuildLlmRequest(
            normalized,
            callerScope,
            bearerToken,
            effectiveModel,
            resolvedRouteValue,
            toolClassification,
            toolContext);

        return ExecutionPlanResult.FromPlan(new ChatCompletionsCreateCommandPlan(
            normalized,
            session,
            llmRequest,
            toolClassification,
            toolPlan.ToolChoiceHintPlan,
            createdAt));
    }

    // Refactor (iter344/cluster-001):
    //   Old pattern: Non-streaming Chat Completions synchronously collected provider output in the HTTP request handler.
    //   New principle: Non-streaming compatibility returns an honest accepted dispatch receipt for the actor run.
    private async Task<ChatCompletionsCreateCommandResult> ExecuteNonStreamingAsync(
        ChatCompletionsCreateCommandPlan plan,
        CancellationToken ct)
    {
        try
        {
            var completion = await ObserveCompletionAsync(plan, null, ct).ConfigureAwait(false);
            return ChatCompletionsCreateCommandResult.FromCompleted(new ChatCompletionsCreateCompletedCommandResult(
                plan.Normalized,
                plan.CreatedAt.ToUnixTimeSeconds(),
                completion));
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ChatCompletionsCreateCommandResult.FromError(401, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ChatCompletionsCreateCommandResult.FromError(ResolveUpstreamStatusCode(ex), ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (ObservedLlmRunCancelledException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return ChatCompletionsCreateCommandResult.FromError(409, "run_cancelled", ex.Message);
        }
        catch (ObservedLlmRunFailedException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ChatCompletionsCreateCommandResult.FromError(500, "llm_run_failed", ex.Message);
        }
        catch (TimeoutException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogWarning(ex, "Non-streaming /v1/chat/completions {CompletionId} timed out waiting for a terminal observation event", plan.Normalized.CompletionId);
            return ChatCompletionsCreateCommandResult.FromError(504, "response_timeout", ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return ChatCompletionsCreateCommandResult.FromError(499, "client_closed_request", "Client closed request.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Unexpected error processing /v1/chat/completions {CompletionId}", plan.Normalized.CompletionId);
            return ChatCompletionsCreateCommandResult.FromError(500, "api_error", "Internal server error.");
        }
    }

    private async Task<LlmSessionCompletionSnapshot> ObserveCompletionAsync(
        ChatCompletionsCreateCommandPlan plan,
        Func<ChatCompletionsObservedDelta, CancellationToken, ValueTask>? onObservedDelta,
        CancellationToken ct)
    {
        using var observationTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        observationTimeoutCts.CancelAfter(_observationTimeout);
        var observeCt = observationTimeoutCts.Token;

        var preparation = await observationScopeLeasePreparationPort
            .PrepareAsync(plan.Session.ActorId, plan.Session.ResponseId, observeCt)
            .ConfigureAwait(false);
        if (preparation == null)
            throw new InvalidOperationException("LlmSession observation scope is unavailable.");

        await using var sink = new EventChannel<EventEnvelope>(capacity: 64);
        EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>? attachment = null;
        try
        {
            try
            {
                attachment = await observationProjectionPort
                    .AttachExistingResponseProjectionAsync(plan.Session.ActorId, plan.Session.ResponseId, sink, observeCt)
                    .ConfigureAwait(false);
                if (attachment == null)
                    throw new InvalidOperationException("LlmSession observation attachment is unavailable.");

                await DispatchRunAsync(plan, observeCt).ConfigureAwait(false);

                var accumulator = new ChatCompletionsObservationAccumulator(plan.Session.ResponseId);
                var terminalObserved = false;
                await foreach (var envelope in sink.ReadAllAsync(observeCt).ConfigureAwait(false))
                {
                    var payload = envelope.Payload;
                    if (payload == null)
                    {
                        continue;
                    }

                    if (payload.Is(LlmStreamChunkObserved.Descriptor))
                    {
                        var observed = payload.Unpack<LlmStreamChunkObserved>();
                        accumulator.ObserveChunk(observed);
                        if (onObservedDelta != null)
                        {
                            await onObservedDelta(
                                new ChatCompletionsObservedDelta(
                                    string.IsNullOrWhiteSpace(observed.DeltaText) ? null : observed.DeltaText,
                                    observed.ToolCallDelta == null ? null : ToBoundaryToolCall(observed.ToolCallDelta),
                                    observed.Usage == null ? null : ToBoundaryUsage(observed.Usage)),
                                ct).ConfigureAwait(false);
                        }

                        continue;
                    }

                    if (payload.Is(LlmRunCompleted.Descriptor))
                    {
                        accumulator.ObserveCompleted(payload.Unpack<LlmRunCompleted>());
                        terminalObserved = true;
                        sink.Complete();
                        break;
                    }

                    if (payload.Is(LlmRunFailed.Descriptor))
                    {
                        var failed = payload.Unpack<LlmRunFailed>();
                        sink.Complete();
                        throw new ObservedLlmRunFailedException(
                            string.IsNullOrWhiteSpace(failed.FailureMessage)
                                ? "LLM run failed."
                                : failed.FailureMessage);
                    }

                    if (payload.Is(LlmRunCancelled.Descriptor))
                    {
                        sink.Complete();
                        throw new ObservedLlmRunCancelledException("LLM run was cancelled.");
                    }
                }

                if (!terminalObserved)
                    throw new InvalidOperationException("LLM run observation ended without a terminal event.");

                return accumulator.BuildCompletion();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && observationTimeoutCts.IsCancellationRequested)
            {
                sink.Complete();
                throw new TimeoutException($"Timed out waiting {FormatObservationTimeout(_observationTimeout)} for the LLM run to emit a terminal event.");
            }
        }
        finally
        {
            if (attachment != null)
            {
                await observationProjectionPort.DetachLiveSinkAsync(attachment.LiveSinkLease, CancellationToken.None)
                    .ConfigureAwait(false);
                await observationProjectionPort.ReleaseActorProjectionAsync(attachment.ProjectionLease, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await observationScopeLeasePreparationPort.ReleaseAsync(preparation, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host parsed catalog route slugs while constructing provider metadata.
    //   New principle: Application resolves model-route command context behind the route resolver port.
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

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host produced provider request objects as the final step before local LLM execution.
    //   New principle: Application produces typed actor-run input while Host remains an external protocol mapper.
    private static LLMRequest BuildLlmRequest(
        NormalizedChatCompletionsCommand normalized,
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
            RequestId = normalized.CompletionId,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
            CallerContext = new LLMRequestCallerContext(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                normalized.CompletionId,
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
            ResponseFormat = normalized.ResponseFormat,
        };
    }

    // Refactor (issue1416-first):
    //   Old pattern: Chat Completions downgraded request identity, caller scope, and bearer credentials into ToolContextMetadata.
    //   New principle: Chat Completions reuses AgentToolExecutionContext as the typed tool execution authority.
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
            new AgentToolCallerContext(callerScope.ScopeId, callerScope.OwnerSubject, responseId),
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

    private static string BuildRouteContentHint(NormalizedChatCompletionsCommand normalized) =>
        normalized.ChatMessages
            .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content
        ?? normalized.ChatMessages.LastOrDefault()?.Content
        ?? string.Empty;

    private static SkillInvocationTrigger? ParseSkillInvocationTrigger(string? text) =>
        SkillInvocationTriggerParser.TryParse(text, platform: "cli", out var trigger)
            ? trigger
            : null;

    private static AgentSkillRecoveryContext BuildSkillRecoveryContext(SkillInvocationTrigger? trigger)
    {
        if (trigger is null)
            return AgentSkillRecoveryContext.Empty;

        if (trigger.IsDiscovery)
        {
            return new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: false,
                CommandName: null,
                OriginalCommand: trigger.OriginalText,
                PrimarySkillName: null,
                MaxOrnnSearchAttempts: 1,
                CommandArguments: null,
                DiscoveryRequested: true);
        }

        return new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: trigger.Name,
            OriginalCommand: trigger.OriginalText,
            PrimarySkillName: trigger.Name,
            MaxOrnnSearchAttempts: 2,
            CommandArguments: trigger.Arguments,
            DiscoveryRequested: false);
    }

    private static string NormalizeRouteCommandName(string? commandName) =>
        string.IsNullOrWhiteSpace(commandName)
            ? string.Empty
            : commandName.Trim().TrimStart('/').ToLowerInvariant();

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host populated session records from endpoint locals.
    //   New principle: Application derives the authoritative session record from normalized command state and caller scope.
    private static LlmSessionRecord BuildSessionRecord(
        NormalizedChatCompletionsCommand normalized,
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

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host updated session failure status around direct provider exceptions.
    //   New principle: Application owns command failure accounting after dispatch attempts.
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

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host executed the LLM loop directly and returned completed output from request-local state.
    //   New principle: Application dispatches a typed run command through IActorDispatchPort and returns the admission.
    private Task<DispatchAdmission> DispatchRunAsync(
        ChatCompletionsCreateCommandPlan plan,
        CancellationToken ct)
    {
        var command = BuildRunRequested(
            plan.Session.ResponseId,
            plan.LlmRequest,
            plan.ToolClassification,
            plan.ToolChoiceHintPlan,
            plan.CreatedAt);
        var envelope = ServiceCommandEnvelopeFactory.Create(
            plan.Session.ActorId,
            command,
            command.RunId);
        return dispatchPort.DispatchAsync(plan.Session.ActorId, envelope, ct);
    }

    // Refactor (iter344/cluster-001):
    //   Old pattern: Chat Completions had no typed actor command payload for its direct LLM loop.
    //   New principle: The facade maps the compatibility request into the shared LlmRunRequested contract.
    private static LlmRunRequested BuildRunRequested(
        string responseId,
        LLMRequest request,
        ResponsesToolClassification toolClassification,
        ResponsesToolChoiceHintPlan toolChoiceHintPlan,
        DateTimeOffset requestedAt)
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
        command.Messages.AddRange(request.Messages.Select(ToRuntimeMessage));
        command.ToolSelection = ToToolSelection(toolClassification, toolChoiceHintPlan);
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
    //   Old pattern: Chat Completions LlmRunRequested persisted tool schemas and hints as JSON strings.
    //   New principle: typed Struct fields carry new writes; JSON strings remain legacy fallback.
    private static LlmSessionRuntimeToolSelection ToToolSelection(
        ResponsesToolClassification classification,
        ResponsesToolChoiceHintPlan toolChoiceHintPlan)
    {
        var selection = new LlmSessionRuntimeToolSelection
        {
            SubstitutedToolNames = { classification.SubstitutedToolNames },
            AdditiveToolNames = { classification.AdditiveToolNames },
        };
        if (!toolChoiceHintPlan.IsEmpty)
        {
            selection.ToolChoiceHintName = toolChoiceHintPlan.ToolName;
            selection.ToolChoiceHintArgumentsJson = toolChoiceHintPlan.PrefilledArgumentsJson();
            selection.ToolChoiceHintArguments = toolChoiceHintPlan.PrefilledArgumentsStruct();
        }

        selection.ForwardedTools.AddRange(classification.ForwardedTools.Select(static tool =>
            new LlmSessionRuntimeToolDeclaration
            {
                ToolName = tool.Name,
                Description = tool.Description,
                ParametersJson = tool.ParametersJson,
                Parameters = ResponsesProtoPayloads.ParseStruct(tool.ParametersJson),
                SchemaHash = tool.SchemaHash,
            }));
        return selection;
    }

    private static int ResolveUpstreamStatusCode(NyxIdUpstreamException ex) =>
        ex.Status switch
        {
            400 => 400,
            401 => 401,
            403 => 403,
            404 => 404,
            429 => 429,
            >= 500 => 502,
            _ => 502,
        };

    private static string FormatObservationTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.FromSeconds(1)
            ? $"{timeout.TotalSeconds:0.#} seconds"
            : $"{timeout.TotalMilliseconds:0} ms";

    private static string NewOpaqueId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static ToolCall ToBoundaryToolCall(LlmSessionRuntimeToolCall call) =>
        new()
        {
            Id = call.CallId,
            Name = call.ToolName,
            ArgumentsJson = RuntimeToolArgumentsJson(call),
        };

    private static string RuntimeToolArgumentsJson(LlmSessionRuntimeToolCall call)
    {
        if (call.Arguments is { Fields.Count: > 0 })
            return ResponsesJsonValues.ToBoundaryJson(Value.ForStruct(call.Arguments.Clone())) ?? "{}";
        return string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
    }

    private static TokenUsage ToBoundaryUsage(LlmSessionTokenUsage usage) =>
        new(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);

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
        ChatCompletionsCreateCommandPlan? Plan,
        ResponsesCommandError? Error)
    {
        public static ExecutionPlanResult FromPlan(ChatCompletionsCreateCommandPlan plan) => new(plan, null);

        public static ExecutionPlanResult FromError(ResponsesCommandError error) => new(null, error);
    }

    private sealed class ObservedLlmRunFailedException(string message) : Exception(message);

    private sealed class ObservedLlmRunCancelledException(string message) : OperationCanceledException(message);

    private sealed class ChatCompletionsObservationAccumulator(string responseId)
    {
        private readonly string _responseId = responseId;
        private string _outputText = string.Empty;
        private readonly List<LlmSessionRuntimeToolCall> _toolCalls = [];
        private TokenUsage? _usage;
        private DateTimeOffset? _completedAt;

        public void ObserveChunk(LlmStreamChunkObserved observed)
        {
            if (!string.IsNullOrWhiteSpace(observed.DeltaText))
                _outputText += observed.DeltaText;

            if (observed.Usage != null)
                _usage = ToBoundaryUsage(observed.Usage);
        }

        public void ObserveCompleted(LlmRunCompleted completed)
        {
            _outputText = completed.OutputText ?? _outputText;
            _usage = completed.Usage == null ? _usage : ToBoundaryUsage(completed.Usage);
            _completedAt = completed.CompletedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
            _toolCalls.Clear();
            _toolCalls.AddRange(completed.ForwardedToolCalls.Select(static call => call.Clone()));
        }

        public LlmSessionCompletionSnapshot BuildCompletion() =>
            new(
                _outputText,
                _toolCalls
                    .Select(static call => new LlmSessionCompletedToolCallSnapshot(
                        call.CallId,
                        call.ToolName,
                        RuntimeToolArgumentsJson(call)))
                    .ToArray(),
                _completedAt ?? DateTimeOffset.UtcNow,
                null,
                null,
                _usage);
    }
}
