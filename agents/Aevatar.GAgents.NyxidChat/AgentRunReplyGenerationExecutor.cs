using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
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
    private const string InvalidGrantRevokeReason = "nyx_invalid_grant";
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IConversationReplyGenerator _replyGenerator;
    private readonly IInteractiveReplyCollector? _interactiveReplyCollector;
    private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdRelayScopeResolver? _scopeResolver;
    private readonly IUserConfigQueryPort? _userConfigQueryPort;
    private readonly INyxIdCapabilityBroker? _capabilityBroker;
    private readonly IBindingRevocationReconciler? _bindingRevocationReconciler;
    private readonly IFileArtifactReadPort? _fileArtifactReadPort;
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
        TimeProvider? timeProvider = null,
        INyxIdCapabilityBroker? capabilityBroker = null,
        IBindingRevocationReconciler? bindingRevocationReconciler = null,
        IFileArtifactReadPort? fileArtifactReadPort = null)
    {
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _interactiveReplyCollector = interactiveReplyCollector;
        _relayOptions = relayOptions;
        _scopeResolver = scopeResolver;
        _userConfigQueryPort = userConfigQueryPort;
        _capabilityBroker = capabilityBroker;
        _bindingRevocationReconciler = bindingRevocationReconciler;
        _fileArtifactReadPort = fileArtifactReadPort;
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
                metadataCts.Token,
                request.TurnCatalog)
                .ConfigureAwait(false);
            var ownerFallbackControl = ResolveInitialOwnerFallbackControl(
                generationContext.OwnerFallbackLlmControl,
                plan.OwnerFallbackLlmControl,
                fallbackToServerDefaultRouting: true);
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

    public async Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
        AgentRunReplyStepExecutionRequest workItem,
        CancellationToken ct)
    {
        // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
        //   Old pattern: AgentRunReplyGenerationExecutor performs LLM/tool IO and constructs the authoritative next AgentRunReplyStepState outside the run actor.
        //   New principle: Executor returns typed IO facts only; AgentRunGAgent applies deterministic step-state transition and persists state inside actor event handling.
        var request = workItem.Request.Clone();
        using TurnStreamingReplySink? streamingSink = TryBuildStreamingSink(request, workItem.RunActorId, request.TargetActorId);
        var streamingState = TryBuildStreamingReplyState(streamingSink);
        var generator = RequireStepGenerator();
        var stepMetadata = AgentRunReplyStepMappers.ToDictionary(workItem.StepState.ExternalMetadata);
        var stepControl = AgentRunReplyStepMappers.LlmControlFromProto(workItem.StepState);
        var planToolContext = AgentRunReplyStepMappers.ToolContextFromProto(workItem.StepState);
        if (workItem.StepState.FinalNoToolsStep)
        {
            // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
            // slash silently consumed.
            // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
            // non-slash text path unchanged (owner-LLM chat fallback).
            planToolContext = ClearSenderBinding(planToolContext);
            if (UsesServerDefaultFallbackRouting(stepControl))
            {
                stepMetadata = StripServerDefaultFallbackMetadata(stepMetadata);
                stepControl = UseServerDefaultRouting(stepControl);
                planToolContext = UseServerDefaultRouting(planToolContext, stepControl);
            }
        }
        (stepControl, planToolContext) = await ReSupplyRuntimeCredentialsAsync(request, stepControl, planToolContext, ct)
            .ConfigureAwait(false);
        var plan = await generator.BuildStepPlanAsync(
                request.Activity!,
                stepMetadata,
                stepControl,
                planToolContext,
                priorHistory: null,
                attachmentContext: null,
                forceDisableTools: workItem.StepState.FinalNoToolsStep,
                ct: ct,
                turnCatalog: workItem.TurnCatalog)
            .ConfigureAwait(false);
        var messages = workItem.StepState.Messages.Select(AgentRunReplyStepMappers.FromProto).ToList();
        var llmRequest = plan.StepExecutor.BuildLlmStepRequest(
            messages,
            request.Activity.Id,
            plan.Metadata,
            plan.ToolContext,
            plan.LlmControl,
            workItem.StepState.Round,
            workItem.StepState.FinalNoToolsStep,
            toolReceipts: workItem.StepState.ToolReceipts);
        llmRequest = await MaterializeFileRefMessagesAsync(llmRequest, ct).ConfigureAwait(false);
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
                        if (!string.IsNullOrEmpty(chunk.DeltaContent))
                        {
                            output.Append(chunk.DeltaContent);
                            if (streamingState is not null)
                                await streamingState.OnDeltaAsync(output.ToString(), token).ConfigureAwait(false);
                        }

                        if (workItem.ReportChunkAsync is not null)
                            await workItem.ReportChunkAsync(chunk, token).ConfigureAwait(false);
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
        if (effectiveToolCalls is { Count: > 0 })
            result.ToolCalls.AddRange(effectiveToolCalls.Select(AgentRunReplyStepMappers.ToProto));

        if (TryTakeOutboundIntent(generator) is { } outboundIntent)
            result.OutboundIntent = outboundIntent.Clone();

        var continuation = new AgentRunNextLlmStepRequestedEvent
        {
            RunId = workItem.RunId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            Attempt = workItem.Attempt,
            StepIndex = workItem.StepIndex + 1,
            Request = request.Clone(),
            LlmStepResult = result,
        };

        AgentRunAuthorizedToolStep? authorizedToolStep = null;
        IReadOnlyList<AgentRunAuthorizedToolCallSafety> authorizedToolCallSafeties = [];
        if (effectiveToolCalls is { Count: > 0 })
        {
            var capturedToolCalls = effectiveToolCalls.ToArray();
            var capturedTools = llmResult.AuthorizedTools.ToArray();
            var capturedToolContext = llmResult.AuthorizedToolContext;
            authorizedToolCallSafeties = BuildAuthorizedToolCallSafeties(
                capturedToolCalls,
                capturedTools);
            authorizedToolStep = new AgentRunAuthorizedToolStep(
                workItem.RunId,
                request.CorrelationId,
                workItem.Attempt,
                continuation.StepIndex,
                result.ToolCalls.ToArray(),
                capturedToolContext,
                async (executionContext, approvalGrant, token) =>
                {
                    using var toolScope = TryBeginInteractiveScope(request);
                    var toolResults = await plan.StepExecutor.ExecuteAuthorizedToolStepAsync(
                            capturedToolCalls,
                            capturedTools,
                            executionContext,
                            token,
                            approvalGrant)
                        .ConfigureAwait(false);
                    var toolStepResult = BuildToolStepResult(toolResults);
                    if (TryTakeOutboundIntent(generator) is { } toolOutboundIntent)
                        toolStepResult.OutboundIntent = toolOutboundIntent.Clone();
                    return toolStepResult;
                });
        }

        return new AgentRunLlmStepExecution(
            continuation,
            authorizedToolStep,
            authorizedToolCallSafeties);
    }

    private static IReadOnlyList<AgentRunAuthorizedToolCallSafety> BuildAuthorizedToolCallSafeties(
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyList<IAgentTool> authorizedTools)
    {
        var snapshots = new List<AgentRunAuthorizedToolCallSafety>(toolCalls.Count);
        foreach (var call in toolCalls)
        {
            var tool = authorizedTools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, call.Name, StringComparison.OrdinalIgnoreCase));
            if (tool is null)
                continue;

            snapshots.Add(new AgentRunAuthorizedToolCallSafety(
                call.Id ?? string.Empty,
                call.Name ?? string.Empty,
                call.ArgumentsJson ?? string.Empty,
                tool.GetCallSafety(call.ArgumentsJson ?? string.Empty),
                tool.SideEffectKind ?? string.Empty));
        }

        return snapshots;
    }

    private async Task<LLMRequest> MaterializeFileRefMessagesAsync(LLMRequest request, CancellationToken ct)
    {
        if (!request.Messages.Any(static message => message.ContentParts?.Any(static part => part.FileRef is not null) == true))
            return request;

        var messages = new List<ChatMessage>(request.Messages.Count);
        foreach (var message in request.Messages)
        {
            if (message.ContentParts is not { Count: > 0 } parts ||
                !parts.Any(static part => part.FileRef is not null))
            {
                messages.Add(message);
                continue;
            }

            messages.Add(new ChatMessage
            {
                Role = message.Role,
                Content = message.Content,
                ReasoningContent = message.ReasoningContent,
                ContentParts = await NyxIdConversationReplyGenerator.MaterializeFileRefPartsAsync(
                        parts,
                        _fileArtifactReadPort,
                        ct)
                    .ConfigureAwait(false),
                ToolCallId = message.ToolCallId,
                ToolCalls = message.ToolCalls,
                ToolResultView = message.ToolResultView,
            });
        }

        return new LLMRequest
        {
            Messages = messages,
            RequestId = request.RequestId,
            Metadata = request.Metadata,
            CallerContext = request.CallerContext,
            ToolContext = request.ToolContext,
            RoutingContext = request.RoutingContext,
            LlmControl = request.LlmControl,
            Tools = request.Tools,
            Model = request.Model,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            ResponseFormat = request.ResponseFormat,
        };
    }

    public async Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
        AgentRunReplyStepExecutionRequest workItem,
        AgentRunAuthorizedToolStep? authorizedToolStep,
        CancellationToken ct)
    {
        // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
        //   Old pattern: AgentRunReplyGenerationExecutor performs LLM/tool IO and constructs the authoritative next AgentRunReplyStepState outside the run actor.
        //   New principle: Executor returns typed IO facts only; AgentRunGAgent applies deterministic step-state transition and persists state inside actor event handling.
        var request = workItem.Request.Clone();
        var toolCalls = workItem.StepState.PendingToolCalls.Select(AgentRunReplyStepMappers.FromProto).ToArray();
        AgentRunToolStepResult toolStepResult;
        if (authorizedToolStep?.Matches(workItem) == true)
        {
            toolStepResult = await authorizedToolStep.ExecuteAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var deniedResults = new List<ToolExecutionResult>(toolCalls.Length);
            foreach (var toolCall in toolCalls)
            {
                deniedResults.Add(new ToolExecutionResult(
                    toolCall.Id,
                    toolCall.Name,
                    JsonSerializer.Serialize(new
                    {
                        error = $"Tool '{toolCall.Name}' is not authorized for this actor-owned step.",
                    }),
                    IsError: true));
            }
            toolStepResult = BuildToolStepResult(deniedResults);
        }

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

    private static AgentRunToolStepResult BuildToolStepResult(
        IReadOnlyList<ToolExecutionResult> results)
    {
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

        ToolResultPayloadBounds.BoundResultMessages(toolStepResult.ResultMessages);
        return toolStepResult;
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

    private TurnStreamingReplySink? TryBuildStreamingSink(
        NeedsLlmReplyEvent request,
        string runActorId,
        string targetActorId)
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
        var streamingTargetActorId = cardMode ? runActorId : targetActorId;
        return new TurnStreamingReplySink(
            _actorDispatchPort,
            streamingTargetActorId,
            request.CorrelationId,
            request.RegistrationId,
            request.Activity.Clone(),
            request.ReplyToken,
            request.ReplyTokenExpiresAtUnixMs,
            request.RunId,
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

        // Re-mint the sender's short-lived NyxID token here, in the deferred reply
        // run. The synchronous inbound path mints a sender token but ConversationGAgent
        // strips transient credentials before persisting NeedsLlmReplyEvent, so by the
        // time this deferred run executes the token is gone. The binding-id + exact
        // typed NyxID authority survive as identity facts, so we re-mint by binding id
        // and overlay the fresh token onto LlmControl; ToToolContext then projects it
        // into ToolContext.Credentials so sender-credentialed mutation tools (use_skill)
        // run under the sender's own NyxID instead of being denied. Owner fallback is
        // derived below with the sender token cleared, so a failed/empty re-mint still
        // leaves the bot-owner LLM path intact.
        control = await ApplySenderTokenAsync(request, toolContext, control, ct).ConfigureAwait(false);

        var ownerFallbackControl = control with { SenderNyxIdAccessToken = null };
        var ownerFallbackToolContext = ClearSenderBinding(toolContext);

        control = OverlayActivityUserToken(request, control);

        return new ReplyGenerationContext(
            metadata,
            control,
            toolContext,
            ownerFallbackControl,
            ownerFallbackToolContext);
    }

    private async Task<LLMControlContext> ApplySenderTokenAsync(
        NeedsLlmReplyEvent request,
        AgentToolExecutionContext toolContext,
        LLMControlContext control,
        CancellationToken ct)
    {
        var broker = _capabilityBroker;
        if (broker is null)
            return control;

        var bindingId = NormalizeOptional(toolContext.SenderBinding.BindingId);
        if (bindingId is null)
            return control;

        if (!TryRebuildSenderSubject(toolContext, out var subject))
        {
            _logger.LogDebug(
                "Sender token re-mint skipped: tool context lacks complete typed NyxID authority. correlation={CorrelationId}",
                request.CorrelationId);
            return control;
        }

        try
        {
            var handle = await broker
                .IssueShortLivedByBindingIdAsync(
                    subject,
                    bindingId,
                    new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy },
                    ct)
                .ConfigureAwait(false);
            var accessToken = NormalizeOptional(handle.AccessToken);
            if (accessToken is null)
            {
                _logger.LogWarning(
                    "Sender NyxID token re-mint returned an empty token; deferred reply run keeps owner fallback. correlation={CorrelationId} subject={Platform}:{Tenant}:{User}",
                    request.CorrelationId,
                    subject.Platform,
                    subject.Tenant,
                    subject.ExternalUserId);
                return control;
            }

            return control with { SenderNyxIdAccessToken = accessToken };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BindingRevokedException ex)
        {
            // Grant is gone upstream (NyxID invalid_grant). Reconcile the local
            // binding so /whoami shows unbound and /init lets the sender re-bind.
            // Best-effort, off the reply path: never await or block the turn.
            _logger.LogWarning(
                ex,
                "Sender NyxID binding revoked at NyxID during deferred re-mint; reconciling local binding and keeping owner fallback. correlation={CorrelationId} subject={Platform}:{Tenant}:{User}",
                request.CorrelationId,
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            TriggerBindingReconcile(subject);
            return control;
        }
        catch (BindingServiceAccessMismatchException ex)
        {
            _logger.LogWarning(
                ex,
                "Sender NyxID binding lacks a required service during deferred re-mint; preserving it until /init service authorization renewal succeeds and keeping owner fallback. correlation={CorrelationId} subject={Platform}:{Tenant}:{User}",
                request.CorrelationId,
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return control;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to re-mint sender NyxID token in deferred reply run; falling back to bot owner LLM config. correlation={CorrelationId} subject={Platform}:{Tenant}:{User}",
                request.CorrelationId,
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return control;
        }
    }

    // Re-supplies runtime credentials onto the token-less per-step control/tool-context read from the
    // persisted (stripped) step-state. Reproduces BuildGenerationContextAsync's derivation from the
    // transient request: owner token from the Activity user token, sender token re-minted from the
    // retained binding identity (choice A — re-mint per step keeps the persisted waterline free of
    // bearer tokens and hands each step a fresh short-lived sender token). On the owner-fallback step
    // the run actor has cleared the Activity token and the sender binding, so the owner token comes
    // from request.LlmControl (the bot-owner token it re-supplied) and no sender token is minted.
    private async Task<(LLMControlContext Control, AgentToolExecutionContext ToolContext)> ReSupplyRuntimeCredentialsAsync(
        NeedsLlmReplyEvent request,
        LLMControlContext stepControl,
        AgentToolExecutionContext planToolContext,
        CancellationToken ct)
    {
        var requestControl = LLMControlContextMapper.FromPayload(request.LlmControl);
        requestControl = await ApplySenderTokenAsync(request, planToolContext, requestControl, ct).ConfigureAwait(false);
        requestControl = OverlayActivityUserToken(request, requestControl);

        var control = stepControl with
        {
            NyxIdAccessToken = requestControl.NyxIdAccessToken,
            NyxIdOrgToken = requestControl.NyxIdOrgToken,
            SenderNyxIdAccessToken = requestControl.SenderNyxIdAccessToken,
        };
        var toolContext = planToolContext with
        {
            Credentials = planToolContext.Credentials with
            {
                NyxIdAccessToken = requestControl.NyxIdAccessToken,
                NyxIdOrgToken = requestControl.NyxIdOrgToken,
                SenderNyxIdAccessToken = requestControl.SenderNyxIdAccessToken,
            },
        };
        return (control, toolContext);
    }

    private static LLMControlContext OverlayActivityUserToken(NeedsLlmReplyEvent request, LLMControlContext control)
    {
        var userAccessToken = NormalizeOptional(request.Activity?.TransportExtras?.NyxUserAccessToken);
        if (userAccessToken is null)
            return control;
        return control with
        {
            NyxIdAccessToken = userAccessToken,
            NyxIdOrgToken = userAccessToken,
        };
    }

    private static bool TryRebuildSenderSubject(
        AgentToolExecutionContext toolContext,
        out ExternalSubjectRef subject)
    {
        subject = new ExternalSubjectRef();
        var authority = toolContext.NyxIdAuthority;
        if (!authority.IsComplete)
            return false;

        var platform = NormalizeOptional(authority.Platform);
        var senderId = NormalizeOptional(authority.ExternalUserId);
        if (platform is null || senderId is null)
            return false;

        // NyxID authority is independent from channel routing identity. Normalize
        // the exact typed authority resolved during inbound binding lookup; never
        // infer it from Channel or SenderBinding convenience fields.
        subject = new ExternalSubjectRef
        {
            Platform = platform.ToLowerInvariant(),
            Tenant = NormalizeOptional(authority.Tenant) ?? string.Empty,
            ExternalUserId = senderId,
        };
        return true;
    }

    private void TriggerBindingReconcile(
        ExternalSubjectRef subject,
        string reason = InvalidGrantRevokeReason)
    {
        var reconciler = _bindingRevocationReconciler;
        if (reconciler is null)
            return;

        var subjectSnapshot = subject.Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                await reconciler
                    .ReconcileRevokedAsync(subjectSnapshot, reason, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort self-heal; reconcile failures must never surface on
                // the reply path. The reconciler logs its own dispatch failures;
                // this only catches unexpected faults so the fire-and-forget task
                // never escapes unobserved.
                _logger.LogWarning(ex, "Binding reconcile after invalid_grant failed (best-effort, ignored).");
            }
        });
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
            var config = await _userConfigQueryPort
                .GetAsync(UserConfigResourceKey.ForOwnerScope(scopeId), ct)
                .ConfigureAwait(false);
            var ownerConfig = new OwnerLlmConfig(
                config.LlmSelection?.Clone() ?? LLMSelectionPolicy.SystemDefaultSelection(),
                LLMSelectionPolicy.ClassifyPersisted(
                    config.LlmSelection,
                    config.PreferredLlmRoute,
                    config.DefaultModel),
                config.MaxToolRounds);
            control = ownerConfig.ApplyTo(control);

            _logger.LogInformation(
                "Applied bot owner LLM config: correlation={CorrelationId} scopeId={ScopeId} status={Status}",
                request.CorrelationId,
                scopeId,
                ownerConfig.Status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMSelectionRepairRequiredException)
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

    private static LLMControlContext UseServerDefaultRouting(LLMControlContext control) =>
        control with
        {
            SenderNyxIdAccessToken = null,
            ModelOverride = null,
            NyxIdRoutePreference = null,
            MaxToolRoundsOverride = null,
        };

    private static bool UsesServerDefaultFallbackRouting(LLMControlContext control) =>
        string.IsNullOrWhiteSpace(control.ModelOverride) &&
        string.IsNullOrWhiteSpace(control.NyxIdRoutePreference) &&
        !control.MaxToolRoundsOverride.HasValue;

    private static AgentToolExecutionContext UseServerDefaultRouting(
        AgentToolExecutionContext context,
        LLMControlContext control)
    {
        var sanitized = ClearSenderBinding(context) with
        {
            Routing = new LLMRequestRoutingContext(
                ModelOverride: null,
                NyxIdRoutePreference: null,
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: NormalizeOptional(control.UserMemoryPrompt) ?? NormalizeOptional(context.Routing.UserMemoryPrompt)),
        };
        return control.ToToolContext(sanitized);
    }

    private static Dictionary<string, string> StripServerDefaultFallbackMetadata(
        IReadOnlyDictionary<string, string> metadata)
    {
        var sanitized = new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        sanitized.Remove(LLMRequestMetadataKeys.SenderBindingId);
        sanitized.Remove(LLMRequestMetadataKeys.SenderNyxIdAccessToken);
        sanitized.Remove(LLMRequestMetadataKeys.ModelOverride);
        sanitized.Remove(LLMRequestMetadataKeys.NyxIdRoutePreference);
        sanitized.Remove(LLMRequestMetadataKeys.MaxToolRoundsOverride);
        return sanitized;
    }

    private static LLMControlContext ResolveInitialOwnerFallbackControl(
        LLMControlContext ownerSnapshot,
        LLMControlContext? planFallback,
        bool fallbackToServerDefaultRouting = false)
    {
        var candidate = planFallback ?? LLMControlContext.Empty;
        return new LLMControlContext(
            NormalizeOptional(ownerSnapshot.NyxIdAccessToken),
            NormalizeOptional(ownerSnapshot.NyxIdOrgToken),
            SenderNyxIdAccessToken: null,
            fallbackToServerDefaultRouting
                ? null
                : NormalizeOptional(candidate.ModelOverride) ?? NormalizeOptional(ownerSnapshot.ModelOverride),
            fallbackToServerDefaultRouting
                ? null
                : NormalizeOptional(candidate.NyxIdRoutePreference) ?? NormalizeOptional(ownerSnapshot.NyxIdRoutePreference),
            fallbackToServerDefaultRouting
                ? null
                : candidate.MaxToolRoundsOverride ?? ownerSnapshot.MaxToolRoundsOverride,
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
            Credentials = source.Credentials with
            {
                NyxIdAccessToken = NormalizeOptional(ownerControl.NyxIdAccessToken),
                NyxIdOrgToken = NormalizeOptional(ownerControl.NyxIdOrgToken),
                SenderNyxIdAccessToken = null,
            },
            Routing = new LLMRequestRoutingContext(
                NormalizeOptional(ownerControl.ModelOverride),
                NormalizeOptional(ownerControl.NyxIdRoutePreference),
                ownerControl.MaxToolRoundsOverride,
                NormalizeOptional(ownerControl.UserMemoryPrompt)),
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

            await _sink.DispatchAsync(text, isFinal, ct).ConfigureAwait(false);
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
