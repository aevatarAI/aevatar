using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

internal sealed class AgentProfileRequiredToolUnavailableException()
    : InvalidOperationException("The sealed Profile readiness tool is unavailable.");

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
    internal const string ToolCatalogPolicyVersion = "agent-turn-tool-catalog/v1";
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IConversationReplyGenerator _replyGenerator;
    private readonly IInteractiveReplyCollector? _interactiveReplyCollector;
    private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdRelayScopeResolver? _scopeResolver;
    private readonly IUserConfigQueryPort? _userConfigQueryPort;
    private readonly INyxIdCapabilityBroker? _capabilityBroker;
    private readonly IBindingRevocationReconciler? _bindingRevocationReconciler;
    private readonly IFileArtifactReadPort? _fileArtifactReadPort;
    private readonly IAgentProfileTurnSnapshotResolver? _profileSnapshotResolver;
    private readonly IAgentProfileTurnToolCatalogPlanner? _profileCatalogPlanner;
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
        IFileArtifactReadPort? fileArtifactReadPort = null,
        IAgentProfileTurnSnapshotResolver? profileSnapshotResolver = null,
        IAgentProfileTurnToolCatalogPlanner? profileCatalogPlanner = null)
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
        _profileSnapshotResolver = profileSnapshotResolver;
        _profileCatalogPlanner = profileCatalogPlanner;
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

            var catalogPlan = await ResolveInitialTurnCatalogAsync(
                    request,
                    replyRequest,
                    generationContext,
                    metadataCts.Token)
                .ConfigureAwait(false);
            var turnCatalog = catalogPlan.Catalog;

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
                turnCatalog)
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
                ToolCatalogProof = turnCatalog.Proof.ToPayload(),
                ToolCatalogPolicyVersion = ToolCatalogPolicyVersion,
            };
            if (catalogPlan.ProfileSnapshot is not null)
                state.AgentProfileSnapshot = catalogPlan.ProfileSnapshot.Clone();
            if (catalogPlan.Authority is not null)
                state.AgentProfileTurnAuthority = catalogPlan.Authority.Clone();
            foreach (var pair in plan.Metadata)
                state.ExternalMetadata[pair.Key] = pair.Value;
            state.Messages.AddRange(plan.InitialMessages.Select(AgentRunReplyStepMappers.ToProto));
            var currentUserMessage = plan.InitialMessages.LastOrDefault(static message =>
                string.Equals(message.Role, "user", StringComparison.Ordinal));
            if (currentUserMessage is not null)
                state.PendingHistoryMessages.Add(AgentRunReplyStepMappers.ToProto(currentUserMessage));
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
        var hasBlockingReceipt = AgentToolReceiptDeliveryPolicy.HasBlockingMutation(
            AgentToolReceiptDeliveryPolicy.Reconcile(workItem.StepState.ToolReceipts));
        var suppressTextStreaming = hasBlockingReceipt && _relayOptions?.StreamingCardKitEnabled != true;
        using TurnStreamingReplySink? streamingSink = suppressTextStreaming
            ? null
            : TryBuildStreamingSink(request, workItem.RunActorId, request.TargetActorId);
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
        var turnCatalog = await ResolvePersistedTurnCatalogAsync(workItem, planToolContext, ct)
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
                turnCatalog: turnCatalog)
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
            toolReceipts: workItem.StepState.ToolReceipts,
            allowMultipleToolCalls: workItem.AllowMultipleToolCalls);
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
                RouteTarget = llmRequest.RouteTarget?.Clone(),
                Tools = null,
                ToolCatalogProof = AgentTurnToolCatalogProof.RestrictedEmpty(
                    llmRequest.ToolCatalogProof?.Budget),
                Model = llmRequest.Model,
                Temperature = llmRequest.Temperature,
                MaxTokens = llmRequest.MaxTokens,
                AllowMultipleToolCalls = llmRequest.AllowMultipleToolCalls,
                ResponseFormat = llmRequest.ResponseFormat,
            };
        }

        var output = new StringBuilder(workItem.StepState.AccumulatedText ?? string.Empty);
        var initialOutputLength = output.Length;
        var skillRecoveryMessages = BuildSkillRecoveryMessages(workItem.StepState);
        var deferSkillRecoveryText = !workItem.StepState.FinalNoToolsStep &&
                                     llmRequest.ToolContext?.SkillRecovery.RequireOrnnSearchOnBlocker == true;
        // A tool-enabled LLM step may emit prose before its tool call. Buffer that step until
        // its terminal shape is known so intermediate planning never becomes a visible reply.
        // This applies to CardKit too: its separate transport does not make tool preambles final.
        var deferPotentialToolCallText = !workItem.StepState.FinalNoToolsStep;
        List<LLMStreamChunk>? deferredLlmChunks = deferSkillRecoveryText || deferPotentialToolCallText
            ? []
            : null;
        LLMStreamChunk? deferredModelCompletion = null;

        async Task DeliverLlmChunkAsync(LLMStreamChunk chunk, CancellationToken token)
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                output.Append(chunk.DeltaContent);
                if (streamingState is not null)
                    await streamingState.OnDeltaAsync(output.ToString(), token).ConfigureAwait(false);
            }

            if (workItem.ReportChunkAsync is not null)
                await workItem.ReportChunkAsync(chunk, token).ConfigureAwait(false);
        }

        ChatRuntimeStepRecoveryToolCall? requiredToolCall = null;
        if (!workItem.StepState.FinalNoToolsStep &&
            workItem.StepState.Round == 0 &&
            turnCatalog.HasUnresolvedConnectedServiceSelectors)
        {
            if (turnCatalog.RequiredToolInvocation is null)
                throw new AgentProfileRequiredToolUnavailableException();

            requiredToolCall = await plan.StepExecutor.TryAuthorizeRequiredToolCallAsync(
                    llmRequest,
                    turnCatalog.RequiredToolInvocation,
                    ct)
                .ConfigureAwait(false);
            if (requiredToolCall is null)
                throw new AgentProfileRequiredToolUnavailableException();
        }

        var recoveryToolCall = requiredToolCall is not null || workItem.StepState.FinalNoToolsStep
            ? null
            : await plan.StepExecutor.TryPlanSkillRecoveryToolCallAsync(
                    llmRequest,
                    skillRecoveryMessages,
                    finalContent: null,
                    ct)
                .ConfigureAwait(false);
        ChatRuntimeStepLlmResult llmResult;
        var modelInvocationStarted = false;
        if (requiredToolCall is not null)
        {
            llmResult = BuildSkillRecoveryLlmResult(requiredToolCall);
        }
        else if (recoveryToolCall is not null)
        {
            llmResult = BuildSkillRecoveryLlmResult(recoveryToolCall);
        }
        else
        {
            llmRequest = await MaterializeFileRefMessagesAsync(llmRequest, ct).ConfigureAwait(false);
            using var interactiveScope = TryBeginInteractiveScope(request);
            llmResult = await plan.StepExecutor.ExecuteLlmStepAsync(
                        plan.StepExecutor.ResolveProvider(),
                        llmRequest,
                        async (chunk, token) =>
                        {
                            if (chunk.LLMInvocationStarted is not null)
                                modelInvocationStarted = true;

                            // The start is committed even while potential tool-call prose stays
                            // hidden. A successful end waits for the round shape so a normal reply
                            // remains ordered START -> visible deltas -> END.
                            if (chunk.LLMInvocationStarted is not null)
                            {
                                await DeliverLlmChunkAsync(chunk, token).ConfigureAwait(false);
                                return;
                            }
                            if (chunk.LLMInvocationCompleted is { Success: true } &&
                                deferredLlmChunks is not null)
                            {
                                deferredModelCompletion = chunk;
                                return;
                            }
                            if (chunk.LLMInvocationCompleted is not null)
                            {
                                await DeliverLlmChunkAsync(chunk, token).ConfigureAwait(false);
                                return;
                            }

                            if (deferredLlmChunks is not null)
                            {
                                deferredLlmChunks.Add(chunk);
                                return;
                            }

                            await DeliverLlmChunkAsync(chunk, token).ConfigureAwait(false);
                        },
                        ct)
                    .ConfigureAwait(false);
        }

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

        if (effectiveToolCalls is not { Count: > 0 } &&
            !workItem.StepState.FinalNoToolsStep &&
            effectiveContent is not null)
        {
            var finalAnswerRecovery = await plan.StepExecutor.TryPlanSkillRecoveryToolCallAsync(
                    llmRequest,
                    skillRecoveryMessages,
                    effectiveContent,
                    ct)
                .ConfigureAwait(false);
            if (finalAnswerRecovery is not null)
            {
                llmResult = BuildSkillRecoveryLlmResult(finalAnswerRecovery, llmResult.Usage);
                effectiveContent = null;
                effectiveToolCalls = llmResult.ToolCalls;
                deferredLlmChunks?.Clear();
            }
        }

        var hasToolCalls = effectiveToolCalls is { Count: > 0 };
        if (hasToolCalls && output.Length > initialOutputLength)
            output.Length = initialOutputLength;

        var approvalRequired = HasApprovalRequiredToolCall(
            effectiveToolCalls,
            llmResult.AuthorizedTools,
            llmResult.AuthorizedToolContext);
        if (deferredLlmChunks is not null && !approvalRequired && !hasToolCalls)
        {
            foreach (var chunk in deferredLlmChunks)
                await DeliverLlmChunkAsync(chunk, ct).ConfigureAwait(false);
        }
        if (deferredModelCompletion is not null)
            await DeliverLlmChunkAsync(deferredModelCompletion, ct).ConfigureAwait(false);
        if (streamingState is not null && !approvalRequired && !hasToolCalls)
            await streamingState.FinalizeAsync(output.ToString(), ct).ConfigureAwait(false);

        var result = new AgentRunLlmStepResult
        {
            AccumulatedText = output.ToString(),
            Content = effectiveContent ?? string.Empty,
            ReasoningContent = llmResult.ReasoningContent ?? string.Empty,
            FinishReason = llmResult.FinishReason ?? string.Empty,
            HasStreamedTextContent = !approvalRequired &&
                                     !hasToolCalls &&
                                     !string.IsNullOrEmpty(effectiveContent),
            ToolRequestId = llmRequest.ToolContext?.Request.RequestId ?? string.Empty,
        };
        if (AgentRunReplyStepMappers.ToProto(llmResult.Usage) is { } usage)
            result.Usage = usage;
        if (effectiveToolCalls is { Count: > 0 })
            result.ToolCalls.AddRange(effectiveToolCalls.Select(AgentRunReplyStepMappers.ToProto));
        if (modelInvocationStarted)
        {
            result.ToolCatalogCaptured = true;
            result.AvailableToolNames.AddRange(llmResult.AuthorizedTools
                .Select(static tool => tool.Name?.Trim() ?? string.Empty)
                .Where(static name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal));
        }

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
                capturedTools,
                capturedToolContext);
            _logger.LogWarning(
                "Agent run LLM step emitted tool calls. runId={RunId} correlation={CorrelationId} step={StepIndex} toolCallCount={ToolCallCount} toolNames={ToolNames} authorizedToolCount={AuthorizedToolCount} authorizedToolNames={AuthorizedToolNames} pendingAuthorizationCount={PendingAuthorizationCount} inputFileRefCount={InputFileRefCount}",
                workItem.RunId,
                request.CorrelationId,
                workItem.StepIndex,
                capturedToolCalls.Length,
                FormatToolNames(capturedToolCalls.Select(static call => call.Name)),
                capturedTools.Length,
                FormatToolNames(capturedTools.Select(static tool => tool.Name)),
                authorizedToolCallSafeties.Count,
                capturedToolContext.InputFileRefs.Count);
            result.PendingToolAuthorizations.AddRange(
                authorizedToolCallSafeties.Select(BuildPendingToolAuthorization));
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

    private static ChatRuntimeStepLlmResult BuildSkillRecoveryLlmResult(
        ChatRuntimeStepRecoveryToolCall recovery,
        TokenUsage? usage = null) =>
        new(
            Content: null,
            ReasoningContent: null,
            ToolCalls: [recovery.ToolCall],
            Terminated: false,
            FinishReason: "tool_calls",
            Usage: usage,
            recovery.AuthorizedTools,
            recovery.AuthorizedToolContext);

    private static IReadOnlyList<ChatMessage> BuildSkillRecoveryMessages(AgentRunReplyStepState stepState)
    {
        if (stepState.PendingHistoryMessages.Count > 0)
        {
            return stepState.PendingHistoryMessages
                .Select(AgentRunReplyStepMappers.FromProto)
                .ToArray();
        }

        return stepState.AppendedHistory
            .Select(AgentRunReplyStepMappers.ToProto)
            .Select(AgentRunReplyStepMappers.FromProto)
            .ToArray();
    }

    private static IReadOnlyList<AgentRunAuthorizedToolCallSafety> BuildAuthorizedToolCallSafeties(
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyList<IAgentTool> authorizedTools,
        AgentToolExecutionContext authorizedToolContext)
    {
        using var toolContextScope = AgentToolContextScope.Push(authorizedToolContext);
        var snapshots = new List<AgentRunAuthorizedToolCallSafety>(toolCalls.Count);
        foreach (var call in toolCalls)
        {
            var tool = authorizedTools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, call.Name, StringComparison.OrdinalIgnoreCase));
            if (tool is null)
                continue;

            var argumentsJson = call.ArgumentsJson ?? string.Empty;
            var providerCallSafety = tool.GetCallSafety(argumentsJson);
            var callSafety = ResolveEffectiveCallSafety(
                tool,
                providerCallSafety);
            var operationAdmission = SnapshotOperationAdmission(tool, argumentsJson);
            snapshots.Add(new AgentRunAuthorizedToolCallSafety(
                call.Id ?? string.Empty,
                call.Name ?? string.Empty,
                argumentsJson,
                callSafety,
                tool.SideEffectKind ?? string.Empty,
                BuildToolDefinitionFingerprint(
                    tool,
                    providerCallSafety,
                    callSafety,
                    operationAdmission),
                ToolPresentationDescriptors.Snapshot(tool, call.Name ?? string.Empty, argumentsJson),
                callSafety.RequiresApproval == true,
                operationAdmission));
        }

        return snapshots;
    }

    private static bool ResolveRequiresApproval(
        IAgentTool tool,
        AgentToolCallSafety safety)
    {
        if (tool.ApprovalMode == ToolApprovalMode.NeverRequire)
            return false;
        return safety.RequiresApproval ??
               (tool.ApprovalMode == ToolApprovalMode.AlwaysRequire ||
                (!safety.IsReadOnly && safety.IsDestructive));
    }

    private static AgentToolCallSafety ResolveEffectiveCallSafety(
        IAgentTool tool,
        AgentToolCallSafety safety) =>
        safety with { RequiresApproval = ResolveRequiresApproval(tool, safety) };

    private static bool HasApprovalRequiredToolCall(
        IReadOnlyList<ToolCall>? toolCalls,
        IReadOnlyList<IAgentTool> authorizedTools,
        AgentToolExecutionContext authorizedToolContext)
    {
        if (toolCalls is not { Count: > 0 })
            return false;

        using var toolContextScope = AgentToolContextScope.Push(authorizedToolContext);
        foreach (var call in toolCalls)
        {
            var tool = authorizedTools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, call.Name, StringComparison.OrdinalIgnoreCase));
            if (tool is null)
                continue;

            var safety = tool.GetCallSafety(call.ArgumentsJson ?? string.Empty);
            if (ResolveRequiresApproval(tool, safety))
                return true;
        }

        return false;
    }

    private static AgentRunPendingToolAuthorization BuildPendingToolAuthorization(
        AgentRunAuthorizedToolCallSafety source) =>
        new()
        {
            Call = new AgentRunToolCall
            {
                Id = source.CallId,
                Name = source.ToolName,
                ArgumentsJson = source.ArgumentsJson,
            },
            HasRequiresApproval = source.CallSafety.RequiresApproval.HasValue,
            RequiresApproval = source.CallSafety.RequiresApproval ?? false,
            IsReadOnly = source.CallSafety.IsReadOnly,
            IsDestructive = source.CallSafety.IsDestructive,
            SideEffectKind = source.SideEffectKind ?? string.Empty,
            ToolDefinitionFingerprint = source.ToolDefinitionFingerprint ?? string.Empty,
            OperationAdmission = source.OperationAdmission?.Clone(),
        };

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
            RouteTarget = request.RouteTarget?.Clone(),
            Tools = request.Tools,
            ToolCatalogProof = request.ToolCatalogProof,
            Model = request.Model,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            AllowMultipleToolCalls = request.AllowMultipleToolCalls,
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
        var transientAuthorizationMatched = authorizedToolStep?.Matches(workItem) == true;
        _logger.LogWarning(
            "Agent run tool step resolving authorization. runId={RunId} correlation={CorrelationId} step={StepIndex} toolCallCount={ToolCallCount} toolNames={ToolNames} transientAuthorizationPresent={TransientAuthorizationPresent} transientAuthorizationMatched={TransientAuthorizationMatched} durableAuthorizationAllowed={DurableAuthorizationAllowed} pendingAuthorizationCount={PendingAuthorizationCount} pendingAuthorizationConsumed={PendingAuthorizationConsumed} inputFileRefCount={InputFileRefCount}",
            workItem.RunId,
            request.CorrelationId,
            workItem.StepIndex,
            toolCalls.Length,
            FormatToolNames(toolCalls.Select(static call => call.Name)),
            authorizedToolStep is not null,
            transientAuthorizationMatched,
            workItem.AllowDurableToolAuthorization,
            workItem.StepState.PendingToolAuthorizations.Count,
            workItem.StepState.PendingToolAuthorizationConsumed,
            AgentRunReplyStepMappers.ToolContextFromProto(workItem.StepState).InputFileRefs.Count);

        AgentRunToolStepResult toolStepResult;
        AgentRunToolAuthorizationOutcome authorizationOutcome;
        if (authorizedToolStep is not null)
        {
            if (transientAuthorizationMatched)
            {
                _logger.LogWarning(
                    "Agent run tool step executing with transient authorization. runId={RunId} correlation={CorrelationId} step={StepIndex} toolNames={ToolNames}",
                    workItem.RunId,
                    request.CorrelationId,
                    workItem.StepIndex,
                    FormatToolNames(toolCalls.Select(static call => call.Name)));
                toolStepResult = await authorizedToolStep.ExecuteAsync(ct).ConfigureAwait(false);
                authorizationOutcome = AgentRunToolAuthorizationOutcome.TransientMatched;
            }
            else
            {
                _logger.LogWarning(
                    "Agent run tool step rejected by transient authorization mismatch. runId={RunId} correlation={CorrelationId} step={StepIndex} toolNames={ToolNames}",
                    workItem.RunId,
                    request.CorrelationId,
                    workItem.StepIndex,
                    FormatToolNames(toolCalls.Select(static call => call.Name)));
                toolStepResult = BuildUnauthorizedToolStepResult(toolCalls);
                authorizationOutcome = AgentRunToolAuthorizationOutcome.Rejected;
            }
        }
        else if (workItem.AllowDurableToolAuthorization &&
                 await TryExecuteDurablyAuthorizedToolStepAsync(workItem, request, toolCalls, ct)
                     .ConfigureAwait(false) is { } durableToolStepResult)
        {
            toolStepResult = durableToolStepResult;
            authorizationOutcome = AgentRunToolAuthorizationOutcome.DurableMatched;
        }
        else
        {
            _logger.LogWarning(
                "Agent run tool step rejected because no matching authorization was available. runId={RunId} correlation={CorrelationId} step={StepIndex} toolNames={ToolNames}",
                workItem.RunId,
                request.CorrelationId,
                workItem.StepIndex,
                FormatToolNames(toolCalls.Select(static call => call.Name)));
            toolStepResult = BuildUnauthorizedToolStepResult(toolCalls);
            authorizationOutcome = AgentRunToolAuthorizationOutcome.Rejected;
        }
        toolStepResult.AuthorizationOutcome = authorizationOutcome;

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

    public async Task<AgentRunNextToolStepRequestedEvent> BuildApprovedToolStepContinuationAsync(
        AgentRunReplyStepExecutionRequest workItem,
        AgentRunPendingToolApprovalState pendingApproval,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pendingApproval);
        var request = workItem.Request.Clone();
        var toolCalls = workItem.StepState.PendingToolCalls.Select(AgentRunReplyStepMappers.FromProto).ToArray();
        var toolStepResult = await TryExecuteDurablyAuthorizedToolStepAsync(
                workItem with { AllowDurableToolAuthorization = true },
                request,
                toolCalls,
                ct,
                pendingApproval)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The approved AgentRun tool capability no longer matches the suspended call.");
        toolStepResult.AuthorizationOutcome =
            AgentRunToolAuthorizationOutcome.DurableMatched;

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

    private async Task<AgentRunToolStepResult?> TryExecuteDurablyAuthorizedToolStepAsync(
        AgentRunReplyStepExecutionRequest workItem,
        NeedsLlmReplyEvent request,
        IReadOnlyList<ToolCall> toolCalls,
        CancellationToken ct,
        AgentRunPendingToolApprovalState? pendingApproval = null)
    {
        if (!TryMatchDurablePendingToolAuthorizations(workItem.StepState, toolCalls, out var authorizations))
        {
            _logger.LogWarning(
                "Agent run durable tool authorization snapshot did not match pending tool calls. runId={RunId} correlation={CorrelationId} step={StepIndex} toolCallCount={ToolCallCount} pendingAuthorizationCount={PendingAuthorizationCount} pendingAuthorizationConsumed={PendingAuthorizationConsumed} toolNames={ToolNames}",
                workItem.RunId,
                request.CorrelationId,
                workItem.StepIndex,
                toolCalls.Count,
                workItem.StepState.PendingToolAuthorizations.Count,
                workItem.StepState.PendingToolAuthorizationConsumed,
                FormatToolNames(toolCalls.Select(static call => call.Name)));
            return null;
        }
        if (request.Activity is null)
        {
            _logger.LogWarning(
                "Agent run durable tool authorization cannot rebuild catalog because request activity is missing. runId={RunId} correlation={CorrelationId} step={StepIndex} toolNames={ToolNames}",
                workItem.RunId,
                request.CorrelationId,
                workItem.StepIndex,
                FormatToolNames(toolCalls.Select(static call => call.Name)));
            return null;
        }

        var generator = RequireStepGenerator();
        var stepMetadata = AgentRunReplyStepMappers.ToDictionary(workItem.StepState.ExternalMetadata);
        var stepControl = AgentRunReplyStepMappers.LlmControlFromProto(workItem.StepState);
        var planToolContext = AgentRunReplyStepMappers.ToolContextFromProto(workItem.StepState);
        if (workItem.StepState.FinalNoToolsStep)
        {
            _logger.LogWarning(
                "Agent run durable tool authorization skipped because step is final no-tools step. runId={RunId} correlation={CorrelationId} step={StepIndex} toolNames={ToolNames}",
                workItem.RunId,
                request.CorrelationId,
                workItem.StepIndex,
                FormatToolNames(toolCalls.Select(static call => call.Name)));
            return null;
        }

        (stepControl, planToolContext) = await ReSupplyRuntimeCredentialsAsync(request, stepControl, planToolContext, ct)
            .ConfigureAwait(false);
        var turnCatalog = await ResolvePersistedTurnCatalogAsync(workItem, planToolContext, ct)
            .ConfigureAwait(false);
        var plan = await generator.BuildStepPlanAsync(
                request.Activity,
                stepMetadata,
                stepControl,
                planToolContext,
                priorHistory: null,
                attachmentContext: null,
                forceDisableTools: false,
                ct: ct,
                turnCatalog: turnCatalog)
            .ConfigureAwait(false);
        var messages = workItem.StepState.Messages.Select(AgentRunReplyStepMappers.FromProto).ToList();
        var toolRequestId = pendingApproval?.ToolRequestId ?? request.Activity.Id;
        var llmRequest = plan.StepExecutor.BuildLlmStepRequest(
            messages,
            toolRequestId,
            plan.Metadata,
            plan.ToolContext,
            plan.LlmControl,
            workItem.StepState.Round,
            finalNoTools: false,
            toolReceipts: workItem.StepState.ToolReceipts,
            allowMultipleToolCalls: workItem.AllowMultipleToolCalls);
        var executionToolContext = llmRequest.ToolContext ?? plan.ToolContext ?? AgentToolExecutionContext.Empty;
        var currentCatalog = llmRequest.Tools ?? [];
        if (!TryMatchCurrentCatalog(toolCalls, authorizations, currentCatalog, executionToolContext, out var admittedTools))
        {
            _logger.LogWarning(
                "Agent run durable tool authorization could not match current catalog. runId={RunId} correlation={CorrelationId} step={StepIndex} toolNames={ToolNames} catalogToolCount={CatalogToolCount} catalogToolNames={CatalogToolNames} inputFileRefCount={InputFileRefCount}",
                workItem.RunId,
                request.CorrelationId,
                workItem.StepIndex,
                FormatToolNames(toolCalls.Select(static call => call.Name)),
                currentCatalog.Count,
                FormatToolNames(currentCatalog.Select(static tool => tool.Name)),
                executionToolContext.InputFileRefs.Count);
            return null;
        }

        AgentToolApprovalGrant? approvalGrant = null;
        if (pendingApproval is not null)
        {
            if (!TryBuildApprovalGrant(workItem, toolCalls, executionToolContext, pendingApproval, out approvalGrant))
            {
                _logger.LogWarning(
                    "Agent run approved tool call failed exact identity validation. runId={RunId} correlation={CorrelationId} step={StepIndex} approvalRequest={ApprovalRequestId}",
                    workItem.RunId,
                    request.CorrelationId,
                    workItem.StepIndex,
                    pendingApproval.ApprovalRequestId);
                return null;
            }
        }

        _logger.LogWarning(
            "Agent run tool step executing with durable authorization. runId={RunId} correlation={CorrelationId} step={StepIndex} toolNames={ToolNames} inputFileRefCount={InputFileRefCount} approvalGrantPresent={ApprovalGrantPresent}",
            workItem.RunId,
            request.CorrelationId,
            workItem.StepIndex,
            FormatToolNames(toolCalls.Select(static call => call.Name)),
            executionToolContext.InputFileRefs.Count,
            approvalGrant is not null);
        using var toolScope = TryBeginInteractiveScope(request);
        var toolResults = await plan.StepExecutor.ExecuteAuthorizedToolStepAsync(
                toolCalls,
                admittedTools,
                executionToolContext,
                ct,
                approvalGrant)
            .ConfigureAwait(false);
        var toolStepResult = BuildToolStepResult(toolResults);
        if (TryTakeOutboundIntent(generator) is { } toolOutboundIntent)
            toolStepResult.OutboundIntent = toolOutboundIntent.Clone();
        return toolStepResult;
    }

    private static bool TryBuildApprovalGrant(
        AgentRunReplyStepExecutionRequest workItem,
        IReadOnlyList<ToolCall> toolCalls,
        AgentToolExecutionContext executionToolContext,
        AgentRunPendingToolApprovalState pending,
        out AgentToolApprovalGrant? approvalGrant)
    {
        approvalGrant = null;
        if (pending.Decision != AgentRunToolApprovalDecision.Approved ||
            toolCalls.Count != 1 ||
            !string.Equals(pending.RunId, workItem.RunId, StringComparison.Ordinal) ||
            !string.Equals(pending.CorrelationId, workItem.Request.CorrelationId, StringComparison.Ordinal) ||
            pending.Attempt != workItem.Attempt ||
            pending.StepIndex != workItem.StepIndex ||
            string.IsNullOrWhiteSpace(pending.ApprovalRequestId) ||
            string.IsNullOrWhiteSpace(pending.ToolRequestId))
        {
            return false;
        }

        var call = toolCalls[0];
        if (!string.Equals(pending.ToolRequestId, executionToolContext.Request.RequestId, StringComparison.Ordinal) ||
            !string.Equals(pending.ToolCallId, call.Id, StringComparison.Ordinal) ||
            !string.Equals(pending.ToolName, call.Name, StringComparison.Ordinal) ||
            !string.Equals(
                pending.ArgumentsSha256,
                AgentToolArgumentsDigest.ComputeSha256(call.ArgumentsJson),
                StringComparison.Ordinal))
        {
            return false;
        }

        approvalGrant = new AgentToolApprovalGrant(
            executionToolContext.ExecutionOwner.Clone(),
            pending.ApprovalRequestId,
            pending.ToolRequestId,
            pending.ToolName,
            pending.ToolCallId,
            pending.ArgumentsSha256);
        return true;
    }

    private static bool TryMatchDurablePendingToolAuthorizations(
        AgentRunReplyStepState stepState,
        IReadOnlyList<ToolCall> toolCalls,
        out IReadOnlyList<AgentRunPendingToolAuthorization> authorizations)
    {
        authorizations = [];
        if (toolCalls.Count == 0 ||
            !stepState.PendingToolAuthorizationConsumed ||
            stepState.PendingToolAuthorizations.Count != toolCalls.Count)
        {
            return false;
        }

        var matched = new List<AgentRunPendingToolAuthorization>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            var snapshot = stepState.PendingToolAuthorizations.FirstOrDefault(candidate =>
                ToolCallMatches(candidate.Call, toolCall));
            if (snapshot is null)
                return false;

            matched.Add(snapshot);
        }

        authorizations = matched;
        return true;
    }

    private static bool TryMatchCurrentCatalog(
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyList<AgentRunPendingToolAuthorization> authorizations,
        IReadOnlyList<IAgentTool> currentCatalog,
        AgentToolExecutionContext executionToolContext,
        out IReadOnlyList<IAgentTool> admittedTools)
    {
        using var toolContextScope = AgentToolContextScope.Push(executionToolContext);
        admittedTools = [];
        var matchedTools = new List<IAgentTool>(toolCalls.Count);
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var toolCall = toolCalls[i];
            var authorization = authorizations[i];
            var tool = currentCatalog.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, toolCall.Name, StringComparison.OrdinalIgnoreCase));
            if (tool is null || !ToolSafetyMatches(authorization, tool, toolCall.ArgumentsJson ?? string.Empty))
                return false;

            matchedTools.Add(tool);
        }

        admittedTools = matchedTools
            .GroupBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        return true;
    }

    private static bool ToolCallMatches(AgentRunToolCall? snapshot, ToolCall toolCall) =>
        snapshot is not null &&
        string.Equals(snapshot.Id, toolCall.Id, StringComparison.Ordinal) &&
        string.Equals(snapshot.Name, toolCall.Name, StringComparison.Ordinal) &&
        string.Equals(snapshot.ArgumentsJson, toolCall.ArgumentsJson, StringComparison.Ordinal);

    private static bool ToolSafetyMatches(
        AgentRunPendingToolAuthorization authorization,
        IAgentTool tool,
        string argumentsJson)
    {
        var providerCallSafety = tool.GetCallSafety(argumentsJson);
        var currentSafety = ResolveEffectiveCallSafety(
            tool,
            providerCallSafety);
        var currentAdmission = SnapshotOperationAdmission(tool, argumentsJson);
        return authorization.HasRequiresApproval == currentSafety.RequiresApproval.HasValue &&
               authorization.RequiresApproval == (currentSafety.RequiresApproval ?? false) &&
               authorization.IsReadOnly == currentSafety.IsReadOnly &&
               authorization.IsDestructive == currentSafety.IsDestructive &&
               string.Equals(authorization.SideEffectKind, tool.SideEffectKind ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(
                   authorization.ToolDefinitionFingerprint,
                   BuildToolDefinitionFingerprint(
                       tool,
                       providerCallSafety,
                       currentSafety,
                       currentAdmission),
                   StringComparison.Ordinal);
    }

    private static string FormatToolNames(IEnumerable<string?> names)
    {
        var values = names
            .Select(NormalizeOptional)
            .Where(static name => name is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? "(none)" : string.Join(',', values);
    }

    private static string BuildToolDefinitionFingerprint(
        IAgentTool tool,
        AgentToolCallSafety providerCallSafety,
        AgentToolCallSafety effectiveCallSafety,
        AgentToolOperationAdmissionPayload? operationAdmission)
    {
        var canonical = string.Join('\n',
            tool.Name ?? string.Empty,
            tool.Description ?? string.Empty,
            tool.ParametersSchema ?? string.Empty,
            tool.SideEffectKind ?? string.Empty,
            ((int)tool.ApprovalMode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            providerCallSafety.RequiresApproval.HasValue ? "1" : "0",
            providerCallSafety.RequiresApproval == true ? "1" : "0",
            effectiveCallSafety.RequiresApproval == true ? "1" : "0",
            effectiveCallSafety.IsReadOnly ? "1" : "0",
            effectiveCallSafety.IsDestructive ? "1" : "0",
            operationAdmission is null
                ? string.Empty
                : Convert.ToBase64String(operationAdmission.ToByteArray()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static AgentToolOperationAdmissionPayload? SnapshotOperationAdmission(
        IAgentTool tool,
        string argumentsJson)
    {
        if (tool is not IAgentToolOperationAdmissionOwner owner)
            return null;

        var payload = (AgentToolExecutionContext.Empty with
        {
            OperationAdmission = owner.ResolveOperationAdmission(argumentsJson),
        }).ToPayload();
        return payload.OperationAdmission?.Clone();
    }

    private static AgentRunToolStepResult BuildUnauthorizedToolStepResult(IReadOnlyList<ToolCall> toolCalls)
    {
        var deniedResults = new List<ToolExecutionResult>(toolCalls.Count);
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

        return BuildToolStepResult(deniedResults);
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
                ToolCallLoop.BuildToolResultMessage(
                    toolResult.CallId,
                    toolResult.ToolName,
                    toolResult.Result,
                    toolResult.Receipt)));
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

    private sealed record AgentRunTurnCatalogPlan(
        AgentTurnToolCatalog Catalog,
        AgentProfileSnapshot? ProfileSnapshot = null,
        AgentProfileTurnAuthorityState? Authority = null);

    private async Task<AgentRunTurnCatalogPlan> ResolveInitialTurnCatalogAsync(
        AgentRunReplyGenerationExecutionRequest request,
        NeedsLlmReplyEvent replyRequest,
        ReplyGenerationContext generationContext,
        CancellationToken ct)
    {
        if (request.TurnCatalog is not null)
            return new AgentRunTurnCatalogPlan(request.TurnCatalog);

        var forward = replyRequest.TargetRef?.ForwardToModel;
        if (forward is null || forward.ProfileKind == ChatRouteAgentProfileKind.Unspecified)
            return new AgentRunTurnCatalogPlan(AgentTurnToolCatalogFactory.RestrictedEmpty());

        if (_profileCatalogPlanner is null)
        {
            throw CatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogNeedsDisambiguation,
                "The agent profile catalog planner is unavailable for this channel turn.");
        }

        AgentProfileSnapshot profile;
        if (replyRequest.AgentProfile is not null)
        {
            if (!AgentProfileSnapshotCodec.Verify(replyRequest.AgentProfile))
            {
                throw CatalogFailure(
                    AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                    "The conversation-pinned channel agent profile snapshot digest is invalid.");
            }

            profile = replyRequest.AgentProfile.Clone();
        }
        else
        {
            if (_profileSnapshotResolver is null)
            {
                throw CatalogFailure(
                    AgentTurnToolCatalogFailureCode.CatalogNeedsDisambiguation,
                    "The agent profile read model is unavailable for this channel turn.");
            }

            var scopeId = NormalizeOptional(generationContext.ToolContext.Caller.ScopeId);
            if (scopeId is null)
            {
                throw new AgentProfileTurnSnapshotResolutionException(
                    AgentProfileTurnSnapshotResolutionStatus.ExplicitReferenceInvalid,
                    "The channel turn has no typed caller scope for agent profile resolution.");
            }

            var resolution = await _profileSnapshotResolver.ResolveAsync(
                    scopeId,
                    request.RunId,
                    forward.ProfileKind,
                    forward.ProfileRef,
                    ct)
                .ConfigureAwait(false);
            if (resolution.Status == AgentProfileTurnSnapshotResolutionStatus.Unprofiled)
                return new AgentRunTurnCatalogPlan(AgentTurnToolCatalogFactory.RestrictedEmpty());
            if (!resolution.IsSelected || resolution.Profile is null)
            {
                throw new AgentProfileTurnSnapshotResolutionException(
                    resolution.Status,
                    "The reviewed agent profile could not be resolved for this channel turn.");
            }

            profile = resolution.Profile.Clone();
        }

        var expectedAgentKind = forward.ProfileKind switch
        {
            ChatRouteAgentProfileKind.WorkspaceChat => AgentProfilePolicies.WorkspaceChatAgentKind,
            ChatRouteAgentProfileKind.ChannelReply => AgentProfilePolicies.ChannelReplyAgentKind,
            ChatRouteAgentProfileKind.NyxidChat => AgentProfilePolicies.NyxIdChatAgentKind,
            _ => null,
        };
        if (expectedAgentKind is null ||
            !string.Equals(profile.AgentKind, expectedAgentKind, StringComparison.Ordinal))
        {
            throw CatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                "The channel route agent kind does not match the pinned agent profile.");
        }

        var routeToolSetName = NormalizeOptional(forward.ToolSetRef?.Name);
        if (routeToolSetName is not null &&
            !string.Equals(routeToolSetName, profile.RouteToolSetRef, StringComparison.Ordinal))
        {
            throw CatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                "The channel route tool-set ceiling does not match the pinned agent profile.");
        }

        var preparation = await _profileCatalogPlanner.PrepareAsync(
                profile,
                request.RunId,
                replyRequest.Activity?.Content?.Text ?? string.Empty,
                [],
                generationContext.ToolContext,
                ct)
            .ConfigureAwait(false);
        if (profile.ActivationMode == AgentProfileActivationMode.Shadow)
        {
            _logger.LogInformation(
                "Channel AgentRun shadow catalog observed without changing model or executor tools. policy={PolicyVersion} profile={ProfileId} revision={PublishedRevision} intent={IntentId} candidateOwned={OwnedCount} candidateSchemaBytes={SchemaBytes} candidateDigest={CatalogDigest}",
                ToolCatalogPolicyVersion,
                profile.ProfileId,
                profile.PublishedRevision,
                preparation.Authority.CandidateRoute?.IntentId ?? string.Empty,
                preparation.ShadowCandidateProof?.ToolCount ?? 0,
                preparation.ShadowCandidateProof?.SchemaBytes ?? 0,
                preparation.ShadowCandidateProof?.CatalogDigest ?? string.Empty);
            return new AgentRunTurnCatalogPlan(
                AgentTurnToolCatalogFactory.RestrictedEmpty(),
                profile);
        }

        var materialization = await _profileCatalogPlanner.MaterializeCommittedAsync(
                profile,
                preparation.Authority,
                generationContext.ToolContext.Credentials.NyxIdAccessToken,
                [],
                generationContext.ToolContext,
                ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Channel AgentRun tool catalog pinned. policy={PolicyVersion} profile={ProfileId} revision={PublishedRevision} intent={IntentId} owned={OwnedCount} schemaBytes={SchemaBytes} digest={CatalogDigest}",
            ToolCatalogPolicyVersion,
            profile.ProfileId,
            profile.PublishedRevision,
            materialization.Catalog.SelectedIntentId ?? materialization.Catalog.CandidateIntentId ?? string.Empty,
            materialization.Catalog.Proof.ToolCount,
            materialization.Catalog.Proof.SchemaBytes,
            materialization.Catalog.Proof.CatalogDigest);
        return new AgentRunTurnCatalogPlan(
            materialization.Catalog,
            profile,
            materialization.ReconcileProposal);
    }

    private async Task<AgentTurnToolCatalog> ResolvePersistedTurnCatalogAsync(
        AgentRunReplyStepExecutionRequest workItem,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        var state = workItem.StepState;
        if (workItem.TurnCatalog is not null)
            return VerifyPersistedTurnCatalog(state, workItem.TurnCatalog);

        var profile = state.AgentProfileSnapshot;
        var authority = state.AgentProfileTurnAuthority;
        if (profile is null && authority is null)
        {
            return VerifyPersistedTurnCatalog(
                state,
                RestrictedEmptyForPersistedProof(state.ToolCatalogProof));
        }
        if (profile is null)
        {
            throw CatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                "The persisted channel turn authority has no pinned agent profile snapshot.");
        }
        if (!AgentProfileSnapshotCodec.Verify(profile))
        {
            throw CatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                "The persisted channel agent profile snapshot digest is invalid.");
        }
        if (authority is null)
        {
            if (profile.ActivationMode != AgentProfileActivationMode.Shadow)
            {
                throw CatalogFailure(
                    AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                    "The persisted active channel profile has no turn authority.");
            }

            return VerifyPersistedTurnCatalog(
                state,
                RestrictedEmptyForPersistedProof(state.ToolCatalogProof));
        }
        if (_profileCatalogPlanner is null)
        {
            throw CatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogNeedsDisambiguation,
                "The agent profile catalog planner is unavailable for channel turn replay.");
        }

        var materialization = await _profileCatalogPlanner.MaterializeCommittedAsync(
                profile,
                authority,
                toolContext.Credentials.NyxIdAccessToken,
                [],
                toolContext,
                ct)
            .ConfigureAwait(false);
        if (!materialization.ReconcileProposal.Equals(authority))
        {
            throw CatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                "The re-materialized channel turn authority differs from its persisted authority.");
        }

        return VerifyPersistedTurnCatalog(state, materialization.Catalog);
    }

    private static AgentTurnToolCatalog RestrictedEmptyForPersistedProof(
        AgentTurnToolCatalogProofPayload? payload)
    {
        var budget = payload is null
            ? AgentTurnToolCatalogBudget.Ordinary
            : AgentTurnToolCatalogProofPayloadMapper.FromPayload(payload).Budget;
        return AgentTurnToolCatalogFactory.RestrictedEmpty(budget);
    }

    private static AgentTurnToolCatalog VerifyPersistedTurnCatalog(
        AgentRunReplyStepState state,
        AgentTurnToolCatalog catalog)
    {
        if (state.ToolCatalogProof is null)
        {
            if (state.AgentProfileSnapshot is not null || state.AgentProfileTurnAuthority is not null)
            {
                throw CatalogFailure(
                    AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                    "The persisted profiled channel turn has no tool catalog proof.");
            }

            return catalog;
        }
        if (!string.Equals(
                state.ToolCatalogPolicyVersion,
                ToolCatalogPolicyVersion,
                StringComparison.Ordinal))
        {
            throw CatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                "The persisted channel tool catalog policy version is unsupported.");
        }

        var persistedProof = AgentTurnToolCatalogProofPayloadMapper.FromPayload(state.ToolCatalogProof);
        persistedProof.AssertMatchesExactTools(catalog.ExactTools.Values);
        if (!catalog.Proof.ToPayload().Equals(state.ToolCatalogProof))
        {
            throw CatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                "The re-materialized channel tool catalog differs from its persisted proof.");
        }

        return catalog;
    }

    private static AgentTurnToolCatalogException CatalogFailure(
        AgentTurnToolCatalogFailureCode code,
        string detail) =>
        new(new AgentTurnToolCatalogFailure(code, detail));

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
    // persisted (stripped) step-state. The transient request carries the typed credential set, the
    // Activity user token may override its owner token, and sender authority is re-minted from the
    // retained binding identity. On the owner-fallback step the run actor has cleared the Activity
    // token and sender binding, so request.LlmControl supplies the bot-owner token.
    private async Task<(LLMControlContext Control, AgentToolExecutionContext ToolContext)> ReSupplyRuntimeCredentialsAsync(
        NeedsLlmReplyEvent request,
        LLMControlContext stepControl,
        AgentToolExecutionContext planToolContext,
        CancellationToken ct)
    {
        var requestCredentials = AgentToolExecutionContextMapper
            .FromPayload(request.ToolContext)
            .Credentials;
        planToolContext = planToolContext with
        {
            Credentials = planToolContext.Credentials with
            {
                NyxIdAccessToken = NormalizeOptional(requestCredentials.NyxIdAccessToken) ??
                                   planToolContext.Credentials.NyxIdAccessToken,
                NyxIdOrgToken = NormalizeOptional(requestCredentials.NyxIdOrgToken) ??
                                planToolContext.Credentials.NyxIdOrgToken,
                SenderNyxIdAccessToken = NormalizeOptional(requestCredentials.SenderNyxIdAccessToken) ??
                                         planToolContext.Credentials.SenderNyxIdAccessToken,
                NyxIdCredentialKind = requestCredentials.NyxIdCredentialKind ==
                                      AgentToolNyxIdCredentialKind.Unspecified
                    ? planToolContext.Credentials.NyxIdCredentialKind
                    : requestCredentials.NyxIdCredentialKind,
                SourceReadableNyxIdAccessToken = NormalizeOptional(
                                                     requestCredentials.SourceReadableNyxIdAccessToken) ??
                                                 planToolContext.Credentials.SourceReadableNyxIdAccessToken,
                NyxIdCredentialAuthority = requestCredentials.NyxIdCredentialAuthority ==
                                           AgentToolNyxIdCredentialAuthority.Unspecified
                    ? planToolContext.Credentials.NyxIdCredentialAuthority
                    : requestCredentials.NyxIdCredentialAuthority,
            },
        };
        var requestControl = LLMControlContextMapper.FromPayload(request.LlmControl);
        requestControl = await ApplySenderTokenAsync(request, planToolContext, requestControl, ct).ConfigureAwait(false);
        requestControl = OverlayActivityUserToken(request, requestControl);

        var control = stepControl with
        {
            NyxIdAccessToken = NormalizeOptional(requestControl.NyxIdAccessToken) ??
                               planToolContext.Credentials.NyxIdAccessToken ??
                               stepControl.NyxIdAccessToken,
            NyxIdOrgToken = NormalizeOptional(requestControl.NyxIdOrgToken) ??
                            planToolContext.Credentials.NyxIdOrgToken ??
                            stepControl.NyxIdOrgToken,
            SenderNyxIdAccessToken = NormalizeOptional(requestControl.SenderNyxIdAccessToken) ??
                                     planToolContext.Credentials.SenderNyxIdAccessToken ??
                                     stepControl.SenderNyxIdAccessToken,
        };
        var toolContext = control.ToToolContext(planToolContext);
        var activityUserToken = NormalizeOptional(request.Activity?.TransportExtras?.NyxUserAccessToken);
        var requestToolContextOwnsCredential = requestCredentials.NyxIdCredentialAuthority ==
                                               AgentToolNyxIdCredentialAuthority.ToolExecutionContext;
        var executionAccessToken = activityUserToken ??
                                   (requestToolContextOwnsCredential
                                       ? NormalizeOptional(requestCredentials.NyxIdAccessToken)
                                       : null);
        var executionOrgToken = activityUserToken ??
                                (requestToolContextOwnsCredential
                                    ? NormalizeOptional(requestCredentials.NyxIdOrgToken)
                                    : null);
        if (executionAccessToken is not null || executionOrgToken is not null)
        {
            // LlmControl owns model routing. Explicit request credentials own tool execution,
            // while a current Activity user token remains the highest-priority user authority.
            toolContext = toolContext with
            {
                Credentials = toolContext.Credentials with
                {
                    NyxIdAccessToken = executionAccessToken ?? toolContext.Credentials.NyxIdAccessToken,
                    NyxIdOrgToken = executionOrgToken ?? toolContext.Credentials.NyxIdOrgToken,
                },
            };
        }
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
