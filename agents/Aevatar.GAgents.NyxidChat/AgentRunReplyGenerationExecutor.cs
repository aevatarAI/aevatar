using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.AI.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

// Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
//   Old pattern: AgentRunReplyGenerationExecutor performs LLM/tool IO and constructs the authoritative next AgentRunReplyStepState outside the run actor.
//   New principle: Executor returns typed IO facts only; AgentRunGAgent applies deterministic step-state transition and persists state inside actor event handling.
// Refactor (iter107/cluster-1-channel-business-io-process-queue):
//   Old pattern: process-local Channel/Task workers owned business IO via singleton executor.
//   New principle: actor-owned operation state (operation_id/lease_epoch/step) + typed self-continuation events; provider IO is inline async, no in-process worker queue.
// Refactor (iter149/issue1132): Old pattern: reply generation carried an optional handled-dispatch adapter for stream chunk delivery.  New principle: executor depends on accepted-only IActorDispatchPort and lets actor events report later completion.
public sealed class AgentRunReplyGenerationExecutor : IAgentRunReplyGenerationExecutorPort
{
    private const string PublisherActorId = "agent-run-reply-generation-executor";
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IConversationReplyGenerator _replyGenerator;
    private readonly IInteractiveReplyCollector? _interactiveReplyCollector;
    private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdRelayScopeResolver? _scopeResolver;
    private readonly IUserConfigQueryPort? _userConfigQueryPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRunReplyGenerationExecutor> _logger;

    public AgentRunReplyGenerationExecutor(
        IActorDispatchPort actorDispatchPort,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? interactiveReplyCollector,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions,
        ILogger<AgentRunReplyGenerationExecutor> logger,
        INyxIdRelayScopeResolver? scopeResolver = null,
        IUserConfigQueryPort? userConfigQueryPort = null,
        TimeProvider? timeProvider = null)
    {
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _interactiveReplyCollector = interactiveReplyCollector;
        _relayOptions = relayOptions;
        _scopeResolver = scopeResolver;
        _userConfigQueryPort = userConfigQueryPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
        AgentRunReplyGenerationExecutionRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var replyRequest = request.Request.Clone();
        using (var metadataCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            metadataCts.CancelAfter(AgentRunGAgent.MetadataBuildBudget);
            ReplyGenerationContext generationContext;
            try
            {
                generationContext = await BuildGenerationContextAsync(replyRequest, metadataCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (metadataCts.IsCancellationRequested)
            {
                throw;
            }

            var generator = RequireStepGenerator();
            var plan = await generator.BuildStepPlanAsync(
                replyRequest.Activity!,
                generationContext.Metadata,
                generationContext.LlmControl,
                generationContext.ToolContext,
                replyRequest.PriorHistory.ToArray(),
                BuildAttachmentInputContext(replyRequest, generationContext.LlmControl),
                forceDisableTools: false,
                metadataCts.Token)
                .ConfigureAwait(false);
            var ownerFallbackControl = ResolveInitialOwnerFallbackControl(
                generationContext.OwnerFallbackLlmControl,
                plan.OwnerFallbackLlmControl);
            var ownerFallbackToolContext = ResolveInitialOwnerFallbackToolContext(
                generationContext.OwnerFallbackToolContext,
                plan.OwnerFallbackToolContext,
                ownerFallbackControl);

            var state = new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = replyRequest.CorrelationId,
                TargetActorId = replyRequest.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                Round = 0,
                MaxToolRounds = plan.MaxToolRounds,
                // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
                // slash silently consumed.
                // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
                // non-slash text path unchanged (owner-LLM chat fallback).
                FinalNoToolsStep = plan.DisableTools,
                LlmControl = plan.LlmControl.ToPayload(),
                ToolContext = plan.ToolContext.ToPayload(),
                OwnerFallbackLlmControl = ownerFallbackControl.ToPayload(),
                OwnerFallbackToolContext = ownerFallbackToolContext.ToPayload(),
            };
            foreach (var pair in plan.Metadata)
                state.ExternalMetadata[pair.Key] = pair.Value;
            state.Messages.AddRange(plan.InitialMessages.Select(AgentRunReplyStepMappers.ToProto));
            return state;
        }
    }

    public Task ExecuteLlmStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var workItem = request with
        {
            Request = request.Request.Clone(),
        };
        return ExecuteLlmStepAndReportAsync(workItem, ct);
    }

    public Task ExecuteToolStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var workItem = request with
        {
            Request = request.Request.Clone(),
        };
        return ExecuteToolStepAndReportAsync(workItem, ct);
    }

    private async Task ExecuteLlmStepAndReportAsync(
        AgentRunReplyStepExecutionRequest workItem,
        CancellationToken ct)
    {
        try
        {
            var command = await BuildLlmStepContinuationAsync(workItem, ct).ConfigureAwait(false);
            await DispatchToRunActorAsync(workItem.RunActorId, command, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (TryBuildOwnerFallbackCommand(workItem, ex) is { } fallback)
        {
            _logger.LogWarning(
                ex,
                "Agent run LLM step failed before completion; retrying with bot owner LLM config and no tools: runId={RunId} correlation={CorrelationId} step={StepIndex}",
                workItem.RunId,
                workItem.Request.CorrelationId,
                workItem.StepIndex);
            await DispatchToRunActorAsync(workItem.RunActorId, fallback, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DispatchStepFailureAsync(workItem, ex).ConfigureAwait(false);
        }
    }

    private async Task ExecuteToolStepAndReportAsync(
        AgentRunReplyStepExecutionRequest workItem,
        CancellationToken ct)
    {
        try
        {
            var command = await BuildToolStepContinuationAsync(workItem, ct).ConfigureAwait(false);
            await DispatchToRunActorAsync(workItem.RunActorId, command, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DispatchStepFailureAsync(workItem, ex).ConfigureAwait(false);
        }
    }

    public async Task<AgentRunNextLlmStepRequestedEvent> BuildLlmStepContinuationAsync(
        AgentRunReplyStepExecutionRequest workItem,
        CancellationToken ct)
    {
        // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
        //   Old pattern: AgentRunReplyGenerationExecutor performs LLM/tool IO and constructs the authoritative next AgentRunReplyStepState outside the run actor.
        //   New principle: Executor returns typed IO facts only; AgentRunGAgent applies deterministic step-state transition and persists state inside actor event handling.
        var request = workItem.Request.Clone();
        using TurnStreamingReplySink? streamingSink = TryBuildStreamingSink(request, request.TargetActorId);
        var streamingState = TryBuildStreamingReplyState(streamingSink);
        var generator = RequireStepGenerator();
        var planToolContext = AgentRunReplyStepMappers.ToolContextFromProto(workItem.StepState);
        if (workItem.StepState.FinalNoToolsStep)
        {
            // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
            // slash silently consumed.
            // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
            // non-slash text path unchanged (owner-LLM chat fallback).
            planToolContext = planToolContext with { SenderBinding = AgentToolSenderBindingContext.Empty };
        }
        var plan = await generator.BuildStepPlanAsync(
                request.Activity!,
                AgentRunReplyStepMappers.ToDictionary(workItem.StepState.ExternalMetadata),
                AgentRunReplyStepMappers.LlmControlFromProto(workItem.StepState),
                planToolContext,
                priorHistory: null,
                attachmentContext: null,
                forceDisableTools: workItem.StepState.FinalNoToolsStep,
                ct: ct)
            .ConfigureAwait(false);
        var messages = workItem.StepState.Messages.Select(AgentRunReplyStepMappers.FromProto).ToList();
        var llmRequest = plan.StepExecutor.BuildLlmStepRequest(
            messages,
            request.Activity.Id,
            plan.Metadata,
            plan.ToolContext,
            plan.LlmControl,
            workItem.StepState.Round,
            workItem.StepState.FinalNoToolsStep);
        if (workItem.StepState.FinalNoToolsStep && llmRequest.Tools is { Count: > 0 })
        {
            // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
            // slash silently consumed.
            // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
            // non-slash text path unchanged (owner-LLM chat fallback).
            llmRequest = new LLMRequest
            {
                Messages = llmRequest.Messages,
                RequestId = llmRequest.RequestId,
                Metadata = llmRequest.Metadata,
                CallerContext = llmRequest.CallerContext,
                ToolContext = llmRequest.ToolContext,
                RoutingContext = llmRequest.RoutingContext,
                LlmControl = llmRequest.LlmControl,
                Tools = null,
                Model = llmRequest.Model,
                Temperature = llmRequest.Temperature,
                MaxTokens = llmRequest.MaxTokens,
                ResponseFormat = llmRequest.ResponseFormat,
            };
        }

        var output = new StringBuilder(workItem.StepState.AccumulatedText ?? string.Empty);
        using var interactiveScope = TryBeginInteractiveScope(request);
        var llmResult = await plan.StepExecutor.ExecuteLlmStepAsync(
                    plan.StepExecutor.ResolveProvider(),
                    llmRequest,
                    async (chunk, token) =>
                    {
                        if (string.IsNullOrEmpty(chunk.DeltaContent))
                            return;
                        output.Append(chunk.DeltaContent);
                        if (streamingState is not null)
                            await streamingState.OnDeltaAsync(output.ToString(), token).ConfigureAwait(false);
                    },
                    ct)
                .ConfigureAwait(false);
        if (streamingState is not null)
            await streamingState.FinalizeAsync(output.ToString(), ct).ConfigureAwait(false);

        // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
        // slash silently consumed.
        // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
        // non-slash text path unchanged (owner-LLM chat fallback).
        var effectiveContent = llmResult.Content;
        var effectiveToolCalls = workItem.StepState.FinalNoToolsStep
            ? []
            : llmResult.ToolCalls;
        if (effectiveToolCalls is not { Count: > 0 } && !workItem.StepState.FinalNoToolsStep && effectiveContent is not null)
        {
            var parsed = TextToolCallParser.Parse(effectiveContent);
            if (parsed.ToolCalls.Count > 0)
            {
                effectiveContent = parsed.CleanedContent;
                effectiveToolCalls = parsed.ToolCalls;
            }
        }

        var result = new AgentRunLlmStepResult
        {
            AccumulatedText = output.ToString(),
            Content = effectiveContent ?? string.Empty,
            ReasoningContent = llmResult.ReasoningContent ?? string.Empty,
            FinishReason = llmResult.FinishReason ?? string.Empty,
            HasStreamedTextContent = !string.IsNullOrEmpty(llmResult.Content),
        };
        if (AgentRunReplyStepMappers.ToProto(llmResult.Usage) is { } usage)
            result.Usage = usage;
        if (TryTakeOutboundIntent(generator) is { } outboundIntent)
            result.OutboundIntent = outboundIntent.Clone();
        if (effectiveToolCalls is { Count: > 0 })
            result.ToolCalls.AddRange(effectiveToolCalls.Select(AgentRunReplyStepMappers.ToProto));

        return new AgentRunNextLlmStepRequestedEvent
        {
            RunId = workItem.RunId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            Attempt = workItem.Attempt,
            StepIndex = workItem.StepIndex + 1,
            Request = request.Clone(),
            LlmStepResult = result,
        };
    }

    public async Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
        AgentRunReplyStepExecutionRequest workItem,
        CancellationToken ct)
    {
        // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
        //   Old pattern: AgentRunReplyGenerationExecutor performs LLM/tool IO and constructs the authoritative next AgentRunReplyStepState outside the run actor.
        //   New principle: Executor returns typed IO facts only; AgentRunGAgent applies deterministic step-state transition and persists state inside actor event handling.
        var request = workItem.Request.Clone();
        var generator = RequireStepGenerator();
        var plan = await generator.BuildStepPlanAsync(
                request.Activity!,
                AgentRunReplyStepMappers.ToDictionary(workItem.StepState.ExternalMetadata),
                AgentRunReplyStepMappers.LlmControlFromProto(workItem.StepState),
                AgentRunReplyStepMappers.ToolContextFromProto(workItem.StepState),
                priorHistory: null,
                attachmentContext: null,
                forceDisableTools: false,
                ct)
            .ConfigureAwait(false);
        var toolCalls = workItem.StepState.PendingToolCalls.Select(AgentRunReplyStepMappers.FromProto).ToArray();
        // Interactive reply tools (reply_with_interaction) execute here, during the tool
        // step — not during the LLM step that emitted the tool calls. The AsyncLocal
        // collector scope does not survive the actor continuation hop between steps, so
        // the tool step must open its own scope for relay turns and return the captured
        // intent as a typed fact on the step result.
        using var interactiveScope = TryBeginInteractiveScope(request);
        var results = await plan.StepExecutor.ExecuteToolStepAsync(toolCalls, plan.Metadata, plan.ToolContext, ct)
            .ConfigureAwait(false);

        var toolStepResult = new AgentRunToolStepResult
        {
            AdvanceRound = true,
        };
        foreach (var toolResult in results)
        {
            toolStepResult.ResultMessages.Add(AgentRunReplyStepMappers.ToProto(
                ToolCallLoop.BuildToolResultMessage(toolResult.CallId, toolResult.ToolName, toolResult.Result)));
            if (toolResult.Receipt is not null)
                toolStepResult.ToolReceipts.Add(toolResult.Receipt.Clone());
        }

        if (TryTakeOutboundIntent(generator) is { } outboundIntent)
            toolStepResult.OutboundIntent = outboundIntent.Clone();

        // Defense-in-depth complement to the Kafka transport fix (commit f2c2319e7):
        // bound the tool-result payload before it enters the
        // AgentRunNextToolStepRequestedEvent command envelope, so an oversized
        // aggregate (e.g. large multi-source JSON) degrades to a truncated-but-useful
        // reply instead of failing the whole run with an opaque ProduceException once
        // the serialized envelope exceeds the broker's max.message.bytes.
        ToolResultPayloadBounds.BoundResultMessages(toolStepResult.ResultMessages);

        return new AgentRunNextToolStepRequestedEvent
        {
            RunId = workItem.RunId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            Attempt = workItem.Attempt,
            StepIndex = workItem.StepIndex + 1,
            Request = request.Clone(),
            ToolStepResult = toolStepResult,
        };
    }

    private IAgentRunStepConversationReplyGenerator RequireStepGenerator() =>
        _replyGenerator as IAgentRunStepConversationReplyGenerator
        ?? throw new InvalidOperationException("Per-step agent run execution requires a step-capable reply generator.");

    private IDisposable? TryBeginInteractiveScope(NeedsLlmReplyEvent request)
    {
        if (_interactiveReplyCollector is null)
            return null;
        if (_relayOptions is not { InteractiveRepliesEnabled: true })
            return null;
        if (!IsRelayRequest(request))
            return null;

        return _interactiveReplyCollector.BeginScope();
    }

    private static bool IsRelayRequest(NeedsLlmReplyEvent request) =>
        request.Activity?.OutboundDelivery is
        {
            ReplyMessageId.Length: > 0,
            CorrelationId.Length: > 0,
        };

    private MessageContent? TryTakeOutboundIntent(IAgentRunStepConversationReplyGenerator generator)
    {
        var typedIntent = generator.TryTakeOutboundIntent();
        var scopedIntent = _interactiveReplyCollector?.TryTake();
        return typedIntent ?? scopedIntent;
    }

    private AgentRunOwnerFallbackStepRequested? TryBuildOwnerFallbackCommand(
        AgentRunReplyStepExecutionRequest workItem,
        Exception ex)
    {
        if (workItem.StepState.FinalNoToolsStep)
            return null;
        if (workItem.StepState.OwnerFallbackLlmControl is null &&
            workItem.StepState.OwnerFallbackToolContext is null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(workItem.StepState.AccumulatedText))
            return null;
        if (!LlmOwnerFallbackPolicy.IsRetryable(ex))
            return null;

        return new AgentRunOwnerFallbackStepRequested
        {
            RunId = workItem.RunId,
            CorrelationId = workItem.Request.CorrelationId,
            TargetActorId = workItem.Request.TargetActorId,
            Attempt = workItem.Attempt,
            StepIndex = workItem.StepIndex + 1,
            Reason = ex.Message ?? string.Empty,
            Request = workItem.Request.Clone(),
        };
    }

    private async Task DispatchStepFailureAsync(AgentRunReplyStepExecutionRequest workItem, Exception ex)
    {
        _logger.LogWarning(
            ex,
            "Agent run reply step executor failed: runId={RunId} correlation={CorrelationId} step={StepIndex}",
            workItem.RunId,
            workItem.Request.CorrelationId,
            workItem.StepIndex);
        var failed = new AgentRunReplyGenerationFailed
        {
            RunId = workItem.RunId,
            CorrelationId = workItem.Request.CorrelationId,
            TargetActorId = workItem.Request.TargetActorId,
            ErrorCode = "llm_reply_failed",
            ErrorSummary = ex.Message,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            Attempt = workItem.Attempt,
            Request = workItem.Request.Clone(),
        };
        try
        {
            await DispatchToRunActorAsync(workItem.RunActorId, failed, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception dispatchEx)
        {
            _logger.LogError(
                dispatchEx,
                "Failed to dispatch agent run step failure command: runId={RunId} actorId={ActorId}",
                workItem.RunId,
                workItem.RunActorId);
        }
    }

    private async Task DispatchToRunActorAsync<TCommand>(
        string runActorId,
        TCommand command,
        CancellationToken ct)
        where TCommand : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = command is AgentRunNextLlmStepRequestedEvent
                    or AgentRunNextToolStepRequestedEvent
                    or AgentRunOwnerFallbackStepRequested
                ? EnvelopeRouteSemantics.CreateTopologyPublication(runActorId, TopologyAudience.Self)
                : EnvelopeRouteSemantics.CreateDirect(PublisherActorId, runActorId),
        };
        await _actorDispatchPort.DispatchAsync(runActorId, envelope, ct).ConfigureAwait(false);
    }

    private TurnStreamingReplySink? TryBuildStreamingSink(NeedsLlmReplyEvent request, string targetActorId)
    {
        if (_relayOptions is not { StreamingRepliesEnabled: true })
            return null;
        if (request.Activity?.OutboundDelivery is not
            {
                ReplyMessageId.Length: > 0,
                CorrelationId.Length: > 0,
            })
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(request.CorrelationId))
            return null;

        var cardMode = _relayOptions.StreamingCardKitEnabled;
        return new TurnStreamingReplySink(
            _actorDispatchPort,
            targetActorId,
            request.CorrelationId,
            request.RegistrationId,
            request.Activity.Clone(),
            request.ReplyToken,
            request.ReplyTokenExpiresAtUnixMs,
            _timeProvider,
            _logger,
            cardMode);
    }

    private StreamingReplyRunState? TryBuildStreamingReplyState(TurnStreamingReplySink? sink)
    {
        if (sink is null || _relayOptions is null)
            return null;

        var cardMode = _relayOptions.StreamingCardKitEnabled;
        var throttle = TimeSpan.FromMilliseconds(Math.Max(0, cardMode
            ? _relayOptions.StreamingCardKitFlushIntervalMs
            : _relayOptions.StreamingFlushIntervalMs));
        var maxInterimChunks = cardMode
            ? int.MaxValue
            : Math.Max(0, _relayOptions.StreamingMaxInterimChunks);

        return new StreamingReplyRunState(sink, throttle, maxInterimChunks, _timeProvider);
    }

    private sealed record ReplyGenerationContext(
        IReadOnlyDictionary<string, string> Metadata,
        LLMControlContext LlmControl,
        AgentToolExecutionContext ToolContext,
        LLMControlContext OwnerFallbackLlmControl,
        AgentToolExecutionContext OwnerFallbackToolContext);

    private async Task<ReplyGenerationContext> BuildGenerationContextAsync(
        NeedsLlmReplyEvent request,
        CancellationToken ct)
    {
        var routedModel = NormalizeOptional(request.TargetRef?.ForwardToModel?.ModelName);
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);

        var control = LLMControlContextMapper.FromPayload(request.LlmControl);
        control = await ApplyBotOwnerLlmConfigAsync(request, control, ct).ConfigureAwait(false);
        if (routedModel is not null)
            control = control with { ModelOverride = routedModel };

        var toolContext = AgentToolExecutionContextMapper.FromPayload(request.ToolContext);
        var ownerFallbackControl = control with { SenderNyxIdAccessToken = null };
        var ownerFallbackToolContext = ClearSenderBinding(toolContext);

        var userAccessToken = request.Activity?.TransportExtras?.NyxUserAccessToken?.Trim();
        if (!string.IsNullOrWhiteSpace(userAccessToken))
        {
            control = control with
            {
                NyxIdAccessToken = userAccessToken,
                NyxIdOrgToken = userAccessToken,
            };
        }

        return new ReplyGenerationContext(
            metadata,
            control,
            toolContext,
            ownerFallbackControl,
            ownerFallbackToolContext);
    }

    private static ChatAttachmentInputContext BuildAttachmentInputContext(
        NeedsLlmReplyEvent request,
        LLMControlContext control)
    {
        var token = NormalizeOptional(control.NyxIdAccessToken)
                    ?? NormalizeOptional(control.NyxIdOrgToken)
                    ?? NormalizeOptional(request.Activity?.TransportExtras?.NyxUserAccessToken);
        return new ChatAttachmentInputContext(
            request.RecentAttachmentActivities.Select(entry => entry.Clone()).ToArray(),
            token);
    }

    private async Task<LLMControlContext> ApplyBotOwnerLlmConfigAsync(
        NeedsLlmReplyEvent request,
        LLMControlContext control,
        CancellationToken ct)
    {
        if (_scopeResolver is null || _userConfigQueryPort is null)
            return control;

        var apiKeyId = request.Activity?.Bot?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(apiKeyId))
            return control;

        string? scopeId;
        try
        {
            scopeId = await _scopeResolver.ResolveScopeIdByApiKeyAsync(apiKeyId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve bot owner scope id for LLM config: correlation={CorrelationId} apiKeyId={ApiKeyId}",
                request.CorrelationId,
                apiKeyId);
            return control;
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            _logger.LogDebug(
                "No bot owner scope id resolved for LLM config: correlation={CorrelationId} apiKeyId={ApiKeyId}",
                request.CorrelationId,
                apiKeyId);
            return control;
        }

        try
        {
            var config = await _userConfigQueryPort.GetAsync(scopeId, ct).ConfigureAwait(false);
            control = control with
            {
                ModelOverride = string.IsNullOrWhiteSpace(config.DefaultModel)
                    ? control.ModelOverride
                    : config.DefaultModel.Trim(),
                NyxIdRoutePreference = string.IsNullOrWhiteSpace(config.PreferredLlmRoute)
                    ? control.NyxIdRoutePreference
                    : config.PreferredLlmRoute.Trim(),
                MaxToolRoundsOverride = config.MaxToolRounds > 0
                    ? config.MaxToolRounds
                    : control.MaxToolRoundsOverride,
            };

            _logger.LogInformation(
                "Applied bot owner LLM config: correlation={CorrelationId} scopeId={ScopeId} model={Model} route={Route}",
                request.CorrelationId,
                scopeId,
                string.IsNullOrWhiteSpace(config.DefaultModel) ? "<server-default>" : config.DefaultModel,
                string.IsNullOrWhiteSpace(config.PreferredLlmRoute) ? "<server-default>" : config.PreferredLlmRoute);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load bot owner LLM config: correlation={CorrelationId} scopeId={ScopeId}",
                request.CorrelationId,
                scopeId);
        }

        return control;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static AgentToolExecutionContext ClearSenderBinding(AgentToolExecutionContext context) =>
        context with
        {
            SenderBinding = AgentToolSenderBindingContext.Empty,
            Credentials = context.Credentials with { SenderNyxIdAccessToken = null },
        };

    private static LLMControlContext ResolveInitialOwnerFallbackControl(
        LLMControlContext ownerSnapshot,
        LLMControlContext? planFallback)
    {
        var candidate = planFallback ?? LLMControlContext.Empty;
        return new LLMControlContext(
            NormalizeOptional(ownerSnapshot.NyxIdAccessToken),
            NormalizeOptional(ownerSnapshot.NyxIdOrgToken),
            SenderNyxIdAccessToken: null,
            NormalizeOptional(candidate.ModelOverride) ?? NormalizeOptional(ownerSnapshot.ModelOverride),
            NormalizeOptional(candidate.NyxIdRoutePreference) ?? NormalizeOptional(ownerSnapshot.NyxIdRoutePreference),
            candidate.MaxToolRoundsOverride ?? ownerSnapshot.MaxToolRoundsOverride,
            NormalizeOptional(candidate.UserMemoryPrompt) ?? NormalizeOptional(ownerSnapshot.UserMemoryPrompt));
    }

    private static AgentToolExecutionContext ResolveInitialOwnerFallbackToolContext(
        AgentToolExecutionContext ownerSnapshot,
        AgentToolExecutionContext? planFallback,
        LLMControlContext ownerControl)
    {
        var source = planFallback ?? ownerSnapshot;
        source = source with
        {
            SenderBinding = AgentToolSenderBindingContext.Empty,
            Credentials = new AgentToolCredentials(
                NormalizeOptional(ownerControl.NyxIdAccessToken),
                NormalizeOptional(ownerControl.NyxIdOrgToken),
                SenderNyxIdAccessToken: null),
        };
        return ownerControl.ToToolContext(source);
    }

    private sealed class StreamingReplyRunState : IStreamingReplySink
    {
        private readonly TurnStreamingReplySink _sink;
        private readonly TimeSpan _throttle;
        private readonly int _maxInterimChunks;
        private readonly TimeProvider _timeProvider;
        private string _lastEmittedText = string.Empty;
        private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
        private int _chunksEmitted;
        private string _pendingText = string.Empty;

        public StreamingReplyRunState(
            TurnStreamingReplySink sink,
            TimeSpan throttle,
            int maxInterimChunks,
            TimeProvider timeProvider)
        {
            _sink = sink;
            _throttle = throttle < TimeSpan.Zero ? TimeSpan.Zero : throttle;
            _maxInterimChunks = maxInterimChunks < 0 ? 0 : maxInterimChunks;
            _timeProvider = timeProvider;
        }

        public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
            TryDispatchAsync(accumulatedText, isFinal: false, ct);

        public Task FinalizeAsync(string finalText, CancellationToken ct) =>
            TryDispatchAsync(finalText, isFinal: true, ct);

        private async Task TryDispatchAsync(string text, bool isFinal, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (string.Equals(text, _lastEmittedText, StringComparison.Ordinal))
            {
                if (isFinal || string.Equals(text, _pendingText, StringComparison.Ordinal))
                    ClearPending();
                return;
            }

            if (!isFinal && _chunksEmitted >= _maxInterimChunks)
            {
                StashPending(text);
                return;
            }

            if (!isFinal)
            {
                var elapsed = _timeProvider.GetUtcNow() - _lastEmitAt;
                if (elapsed < _throttle)
                {
                    StashPending(text);
                    return;
                }
            }

            await _sink.DispatchAsync(text, ct).ConfigureAwait(false);
            if (_sink.ChunksEmitted > _chunksEmitted)
            {
                _lastEmittedText = text;
                _lastEmitAt = _timeProvider.GetUtcNow();
                _chunksEmitted = _sink.ChunksEmitted;
                if (isFinal || string.Equals(_pendingText, text, StringComparison.Ordinal))
                    ClearPending();
            }
        }

        private void StashPending(string text)
        {
            _pendingText = text;
        }

        private void ClearPending()
        {
            _pendingText = string.Empty;
        }
    }
}
