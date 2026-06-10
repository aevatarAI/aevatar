using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Run-scoped continuation owner for one deferred channel LLM reply.
/// </summary>
// Refactor (iter20/cluster-004):
//   Old pattern: ConversationGAgent 持有 actor token registry + 可见回复状态部分仅在内存
//   New principle: 删 actor token registry,credentials runtime-only,可见回复 lifecycle 持久到 ConversationGAgent state
// Refactor (iter73/cluster-073-durable-callback-runtime-credentials):
//   Old pattern: durable callback envelope clones full command/chunk payload, may embed transient runtime credentials (reply_token)
//   New principle: callback payload carries only stable IDs + actor-owned lease keys; actor reconciles from current actor state on fire
// Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
//   Old pattern: AgentRunReplyGenerationExecutor performs LLM/tool IO and constructs the authoritative next AgentRunReplyStepState outside the run actor.
//   New principle: Executor returns typed IO facts only; AgentRunGAgent applies deterministic step-state transition and persists state inside actor event handling.
// Refactor (iter107/cluster-1-channel-business-io-process-queue):
//   Old pattern: process-local Channel/Task workers owned business IO via singleton executor.
//   New principle: actor-owned operation state (operation_id/lease_epoch/step) + typed self-continuation events; provider IO is inline async, no in-process worker queue.
[GAgent("nyxid.chat.agent-run")]
public sealed class AgentRunGAgent : GAgentBase<AgentRunGAgentState>
{
    internal const long MaxRunRequestAgeMs = 5 * 60 * 1000;

    /// <summary>
    /// Hard upper bound on a single LLM reply turn. Mirrors
    /// <c>NyxIdRelayOptions.ResponseTimeoutSeconds</c> (default 300s).
    /// A configured value of <c>0</c> or negative is treated as "disable the cap".
    /// </summary>
    internal const int FallbackTimeoutSecondsDefault = 300;

    /// <summary>
    /// Standalone budget for metadata enrichment (scope resolve + UserConfig lookup).
    /// </summary>
    internal static readonly TimeSpan MetadataBuildBudget = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan TerminalCleanupDelay = TimeSpan.FromMinutes(5);
    private const string TerminalCleanupCallbackPrefix = "agent-run-terminal-cleanup";
    private const string GenerationTimeoutCallbackPrefix = "agent-run-generation-timeout";
    internal static readonly TimeSpan OutputDispatchRetryDelay = TimeSpan.FromSeconds(5);
    private const string OutputDispatchRetryCallbackPrefix = "agent-run-output-dispatch-retry";
    internal static readonly TimeSpan DropNotificationRetryDelay = TimeSpan.FromSeconds(5);
    private const string DropNotificationRetryCallbackPrefix = "agent-run-drop-notification-retry";

    private readonly IActorRuntime _actorRuntime;
    private readonly IAgentRunReplyGenerationExecutorPort _generationExecutor;
    private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly IActorRuntimeCallbackScheduler? _callbackScheduler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRunGAgent> _logger;
    private readonly IAgentToolReceiptRenderer _toolReceiptRenderer;

    public AgentRunGAgent(
        IActorRuntime actorRuntime,
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions,
        ILogger<AgentRunGAgent> logger,
        IActorRuntimeCallbackScheduler? callbackScheduler = null,
        TimeProvider? timeProvider = null,
        IAgentToolReceiptRenderer? toolReceiptRenderer = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _generationExecutor = generationExecutor ?? throw new ArgumentNullException(nameof(generationExecutor));
        _relayOptions = relayOptions;
        _callbackScheduler = callbackScheduler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _toolReceiptRenderer = toolReceiptRenderer ?? new AgentToolReceiptRenderer();
    }

    protected override AgentRunGAgentState TransitionState(AgentRunGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<AgentRunStartedEvent>(ApplyStarted)
            .On<AgentRunReplyGenerationRequestedEvent>(ApplyReplyGenerationRequested)
            .On<AgentRunReplyStepStateUpdatedEvent>(ApplyReplyStepStateUpdated)
            .On<AgentRunReplyProducedEvent>(ApplyReplyProduced)
            .On<AgentRunReplyDispatchedEvent>(ApplyReplyDispatched)
            .On<AgentRunDroppedEvent>(ApplyDropped)
            .On<AgentRunDropNotificationDispatchedEvent>(ApplyDropNotificationDispatched)
            .On<AgentRunFailedEvent>(ApplyFailed)
            .On<AgentRunCleanupCompletedEvent>(ApplyCleanupCompleted)
            .OrCurrent();

    // ADR-0021 §6 / canon §9 absorbing-terminal check. Combined with
    // `cleanup_completed_at_unix_ms != 0` this defines chain.finalized.
    // Every reply-ready / dropped / failed / cleanup handler MUST short-circuit
    // on a terminal status; late / stale signals must no-op.
    internal static bool IsTerminal(AgentRunStatus status) =>
        status is AgentRunStatus.Dropped
               or AgentRunStatus.Failed
               or AgentRunStatus.ReplyHandedOff;

    private bool IsTerminal() => IsTerminal(State.Status);

    private bool IsCleanupAlreadyCompleted() => State.CleanupCompletedAtUnixMs != 0;

    [EventHandler]
    public async Task HandleStartAsync(AgentRunStartRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Request is null)
        {
            _logger.LogWarning("Dropping malformed agent run start command without request: runActor={RunActorId}", Id);
            return;
        }

        // Refactor (iter101/cluster-105): Old=run_id could be recovered by parsing the run actor id; New=run_id is command data.
        if (!AgentRunId.TryParse(command.RunId, out var typedCommandRunId))
        {
            _logger.LogWarning(
                "Dropping malformed agent run start command without typed run_id: runActor={RunActorId} correlation={CorrelationId}",
                Id,
                command.Request.CorrelationId);
            return;
        }

        var commandRunId = typedCommandRunId.Value;
        var request = command.Request.Clone();
        ApplyTargetRefOverrides(request);
        if (!string.IsNullOrWhiteSpace(request.RunId) &&
            !string.Equals(request.RunId.Trim(), commandRunId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Dropping malformed agent run start command with mismatched run_id: commandRunId={CommandRunId} requestRunId={RequestRunId} runActor={RunActorId}",
                commandRunId,
                request.RunId,
                Id);
            return;
        }

        request.RunId = commandRunId;
        if (!AgentRunId.TryParse(request.RunId, out _))
        {
            // Refactor (iter98/cluster-002): Old=missing run_id fell back to correlation/actor id; New=start commands without explicit run_id are malformed.
            _logger.LogWarning(
                "Dropping malformed agent run start command without run_id: runActor={RunActorId} correlation={CorrelationId}",
                Id,
                request.CorrelationId);
            return;
        }

        var runId = ResolveRunId(request);
        var startedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // ADR-0021 chain.finalized precondition: terminal status means the run has
        // already dropped, failed, or handed the reply off. Late starts must no-op
        // beyond (re-)scheduling cleanup — never re-run the LLM / tool chain.
        // Cleanup is itself idempotent on `cleanup_completed_at != 0`.
        if (IsTerminal())
        {
            _logger.LogInformation(
                "Ignoring duplicate terminal agent run start: runId={RunId} status={Status} cleanupCompleted={CleanupCompleted}",
                runId,
                State.Status,
                IsCleanupAlreadyCompleted());
            if (!IsCleanupAlreadyCompleted())
                await ScheduleTerminalCleanupAsync(NormalizeOptional(State.RunId) ?? runId);
            return;
        }

        // ReplyProduced but not yet handed off: this is the output-dispatch retry path —
        // re-deliver the persisted payload without re-running the LLM / tool chain so
        // we don't repeat tool side effects (SSH exec, external API calls, billing)
        // or produce a different reply.
        if (State.Status is AgentRunStatus.ReplyProduced)
        {
            _logger.LogInformation(
                "Re-dispatching previously produced reply (output-dispatch retry): runId={RunId} correlation={CorrelationId}",
                runId,
                request.CorrelationId);
            try
            {
                await ReDispatchProducedReplyAsync(request, runId);
            }
            catch (AgentRunOutputDispatchException ex)
            {
                if (!await TryHandleOutputDispatchFailureAsync(request, runId, ex))
                    throw;
            }
            return;
        }

        if (State.Status is AgentRunStatus.ReplyGenerationRequested)
        {
            _logger.LogInformation(
                "Ignoring duplicate agent run start while reply generation is already requested: runId={RunId} correlation={CorrelationId}",
                runId,
                request.CorrelationId);
            return;
        }

        if (string.IsNullOrWhiteSpace(State.RunId))
        {
            await PersistDomainEventAsync(new AgentRunStartedEvent
            {
                RunId = runId,
                CorrelationId = request.CorrelationId,
                TargetActorId = request.TargetActorId,
                StartedAtUnixMs = startedAtUnixMs,
            });
        }

        try
        {
            await RequestReplyGenerationAsync(request, runId);
        }
        catch (AgentRunOutputDispatchException ex)
        {
            if (await TryHandleOutputDispatchFailureAsync(request, runId, ex))
                return;

            await PersistFailedAsync(
                request,
                runId,
                "agent_run_output_dispatch_failed",
                ex.Message);
        }
        catch (Exception ex)
        {
            await FailAfterUnexpectedExceptionAsync(request, runId, ex);
        }
    }

    [EventHandler]
    public async Task HandleOutputDispatchRetryAsync(AgentRunOutputDispatchRetryRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!IsCurrentOutputDispatchRetry(command))
            return;

        var request = BuildOutputDispatchRetryRequest(command);
        if (command.RequiresRuntimeReplyToken)
        {
            _logger.LogWarning(
                "Dropping durable output-dispatch retry without runtime reply_token: runId={RunId} correlation={CorrelationId}",
                command.RunId,
                command.CorrelationId);
            await PersistFailedAsync(
                request,
                command.RunId,
                "missing_relay_reply_token_for_durable_retry",
                "Durable output-dispatch retry cannot rehydrate runtime-only relay reply_token.");
            return;
        }

        try
        {
            await ReDispatchProducedReplyAsync(request, command.RunId);
        }
        catch (AgentRunOutputDispatchException ex)
        {
            if (!await TryHandleOutputDispatchFailureAsync(request, command.RunId, ex))
                throw;
        }
    }

    [EventHandler]
    public async Task HandleDropNotificationRetryAsync(AgentRunDropNotificationRetryRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!IsCurrentDropNotificationRetry(command))
            return;

        try
        {
            await DispatchPendingDropNotificationAsync();
            await TryPersistDropNotificationDispatchedAsync();
        }
        catch (AgentRunOutputDispatchException ex)
        {
            await TryScheduleDropNotificationRetryAsync(command.Attempt + 1, ex);
        }
    }

    [EventHandler]
    public async Task HandleReplyGenerationFailedAsync(AgentRunReplyGenerationFailed command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsCurrentGenerationContinuation(command.RunId, command.CorrelationId, command.Attempt))
            return;

        var request = command.Request?.Clone() ?? new NeedsLlmReplyEvent
        {
            CorrelationId = command.CorrelationId,
            TargetActorId = command.TargetActorId,
            RunId = command.RunId,
        };
        ApplyTargetRefOverrides(request);
        if (string.IsNullOrWhiteSpace(request.TargetActorId))
            request.TargetActorId = command.TargetActorId;

        var errorCode = NormalizeOptional(command.ErrorCode) ?? "agent_run_generation_failed";
        var errorSummary = command.ErrorSummary ?? string.Empty;
        try
        {
            await ProduceAndDispatchAsync(
                request,
                command.RunId,
                "Sorry, I wasn't able to generate a response. Please try again.",
                null,
                LlmReplyTerminalState.Failed,
                errorCode,
                errorSummary);
        }
        catch (AgentRunOutputDispatchException ex)
        {
            if (await TryHandleOutputDispatchFailureAsync(request, command.RunId, ex))
                return;

            await PersistFailedAsync(request, command.RunId, errorCode, errorSummary);
        }
        catch (Exception ex)
        {
            await FailAfterUnexpectedExceptionAsync(request, command.RunId, ex);
        }
    }

    [EventHandler]
    public async Task HandleReplyGenerationTimedOutAsync(AgentRunReplyGenerationTimedOut command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsCurrentGenerationContinuation(command.RunId, command.CorrelationId, command.Attempt))
            return;

        await DispatchGenerationTimeoutDropNotificationAsync(command);

        await PersistDomainEventAsync(new AgentRunFailedEvent
        {
            RunId = command.RunId,
            CorrelationId = command.CorrelationId,
            TargetActorId = command.TargetActorId,
            ErrorCode = "llm_reply_timeout",
            ErrorSummary = $"LLM reply generation exceeded {(int)ResolveFallbackTimeout().TotalSeconds}s budget.",
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await ScheduleTerminalCleanupAsync(command.RunId);
    }

    [EventHandler]
    public async Task HandleCleanupAsync(AgentRunCleanupRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ADR-0021 §6 / canon §9 — cleanup is an absorbing operation. It is only
        // valid for runs that have reached terminal status; stale runId references
        // (the actor identity changed under us) and late callbacks (cleanup already
        // completed) must both no-op so duplicates do not destroy a fresh run.
        if (!IsTerminal())
            return;

        if (!string.IsNullOrWhiteSpace(command.RunId) &&
            !string.IsNullOrWhiteSpace(State.RunId) &&
            !string.Equals(command.RunId, State.RunId, StringComparison.Ordinal))
        {
            return;
        }

        if (IsCleanupAlreadyCompleted())
        {
            _logger.LogDebug(
                "Ignoring duplicate terminal cleanup: runId={RunId} cleanupCompletedAtUnixMs={CleanupAt}",
                NormalizeOptional(State.RunId) ?? command.RunId,
                State.CleanupCompletedAtUnixMs);
            return;
        }

        await PersistDomainEventAsync(new AgentRunCleanupCompletedEvent
        {
            RunId = NormalizeOptional(State.RunId) ?? command.RunId ?? string.Empty,
            CorrelationId = State.CorrelationId ?? string.Empty,
            CompletedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await _actorRuntime.DestroyAsync(Id, CancellationToken.None);
    }

    private async Task RequestReplyGenerationAsync(NeedsLlmReplyEvent request, string runId)
    {
        _logger.LogInformation(
            "Requesting agent run LLM reply generation: runId={RunId} correlation={CorrelationId} target={TargetActorId}",
            runId,
            request.CorrelationId,
            request.TargetActorId);

        if (request.Activity is null || string.IsNullOrWhiteSpace(request.TargetActorId))
        {
            _logger.LogWarning(
                "Dropping malformed deferred LLM reply request: runId={RunId}, correlation={CorrelationId}, target={TargetActorId}",
                runId,
                request.CorrelationId,
                request.TargetActorId);
            await DropAsync(request, runId, "malformed_deferred_llm_reply_request");
            return;
        }

        // Stale gate: NyxID relay reply tokens have a ~30 min TTL and the user access
        // token used for the LLM call expires inside ~15 min. A request that has been
        // delayed past the run window cannot lead to a successful reply.
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (request.RequestedAtUnixMs > 0 && nowMs - request.RequestedAtUnixMs > MaxRunRequestAgeMs)
        {
            _logger.LogInformation(
                "Dropping stale LLM reply request: runId={RunId} correlation={CorrelationId} ageMs={AgeMs}",
                runId,
                request.CorrelationId,
                nowMs - request.RequestedAtUnixMs);
            await DropAsync(request, runId, "stale_agent_run_request_dropped");
            return;
        }

        // Relay credential gate: relay turns require a fresh reply_token to send the
        // outbound. A relay request with no command-carried token cannot be delivered,
        // so skip the LLM call entirely.
        if (IsRelayRequest(request) && string.IsNullOrWhiteSpace(request.ReplyToken))
        {
            _logger.LogWarning(
                "Dropping relay LLM reply request without command-carried reply_token: runId={RunId} correlation={CorrelationId}",
                runId,
                request.CorrelationId);
            await DropAsync(request, runId, "missing_relay_reply_token");
            return;
        }

        await EnsureTargetActorAsync(request.TargetActorId);

        var requestedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var attempt = Math.Max(1, State.GenerationAttempt + 1);
        await PersistDomainEventAsync(new AgentRunReplyGenerationRequestedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            RequestedAtUnixMs = requestedAtUnixMs,
            Attempt = attempt,
        });

        await ScheduleGenerationTimeoutAsync(request, runId, attempt);
        await StartPerStepReplyGenerationAsync(request, runId, attempt);
    }

    private async Task StartPerStepReplyGenerationAsync(NeedsLlmReplyEvent request, string runId, int attempt)
    {
        var generationContext = await BuildInitialStepStateAsync(request, runId, attempt);
        await PersistStepStateAsync(generationContext);
        await PublishAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            Attempt = attempt,
            StepIndex = generationContext.NextStepIndex,
            Request = request.Clone(),
        }, TopologyAudience.Self, CancellationToken.None);
    }

    private async Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
        NeedsLlmReplyEvent request,
        string runId,
        int attempt)
    {
        // Refactor (iter99/cluster-596-phase-e): Old pattern: ChatRuntime owned round/tool loop state in one executor call.
        // New principle: AgentRunGAgent persists the per-step waterline and advances through typed self-messages.
        return await _generationExecutor.BuildInitialStepStateAsync(
                new AgentRunReplyGenerationExecutionRequest(runId, Id, attempt, request.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task PersistStepStateAsync(AgentRunReplyStepState stepState)
    {
        await PersistDomainEventAsync(new AgentRunReplyStepStateUpdatedEvent
        {
            RunId = stepState.RunId,
            CorrelationId = stepState.CorrelationId,
            TargetActorId = stepState.TargetActorId,
            Attempt = stepState.Attempt,
            StepState = stepState.Clone(),
        });
    }

    private async Task DispatchLlmStepExecutorAsync(NeedsLlmReplyEvent request, AgentRunReplyStepState stepState)
    {
        await _generationExecutor.ExecuteLlmStepAsync(
            new AgentRunReplyStepExecutionRequest(
                stepState.RunId,
                Id,
                stepState.Attempt,
                stepState.NextStepIndex,
                request.Clone(),
                stepState.Clone()),
            CancellationToken.None);
    }

    private async Task DispatchToolStepExecutorAsync(NeedsLlmReplyEvent request, AgentRunReplyStepState stepState)
    {
        await _generationExecutor.ExecuteToolStepAsync(
            new AgentRunReplyStepExecutionRequest(
                stepState.RunId,
                Id,
                stepState.Attempt,
                stepState.NextStepIndex,
                request.Clone(),
                stepState.Clone()),
            CancellationToken.None);
    }

    private async Task CompletePerStepReplyAsync(NeedsLlmReplyEvent request, AgentRunReplyStepState stepState)
    {
        var hasReplyText = !string.IsNullOrWhiteSpace(stepState.AccumulatedText);
        var emptyReplyDiagnostics = hasReplyText ? string.Empty : BuildEmptyReplyDiagnostics(stepState);
        if (!hasReplyText)
        {
            // The empty-reply terminal path is otherwise silent: unlike the executor's
            // exception path (DispatchStepFailureAsync), a step that *completes* with no
            // assistant text fails the run with a generic echo while the signals that
            // explain why the content was empty (finish reason, reasoning-only output,
            // token-budget exhaustion) are discarded. Surface them so the failure is
            // diagnosable from logs and the persisted terminal event.
            _logger.LogWarning(
                "Agent run completed with empty reply (terminal failure): runId={RunId} correlation={CorrelationId} {Diagnostics}",
                NormalizeOptional(stepState.RunId) ?? "(none)",
                NormalizeOptional(stepState.CorrelationId) ?? "(none)",
                emptyReplyDiagnostics);
        }

        await ProduceAndDispatchAsync(
            request,
            stepState.RunId,
            hasReplyText
                ? stepState.AccumulatedText
                : "Sorry, I wasn't able to generate a response. Please try again.",
            stepState.OutboundIntent?.Clone(),
            hasReplyText ? LlmReplyTerminalState.Completed : LlmReplyTerminalState.Failed,
            hasReplyText ? string.Empty : "empty_reply",
            hasReplyText ? string.Empty : $"Reply generator returned an empty response ({emptyReplyDiagnostics}).",
            stepState.AppendedHistory.ToArray(),
            stepState.ToolReceipts.ToArray(),
            stepState.PendingToolCalls.ToArray());
    }

    // Diagnostic context for the otherwise-silent empty-reply terminal path. Reads only
    // signals already captured on the step state (finish reason, streamed-text flag,
    // reasoning presence, tool-round position, token usage) — no message content, so it
    // is safe to log and persist. reasoningOnly=true is the smoking gun for a reasoning
    // model that spent its output budget on reasoning tokens and emitted no answer text.
    private static string BuildEmptyReplyDiagnostics(AgentRunReplyStepState stepState)
    {
        var hasReasoning = stepState.Messages.Any(message =>
            string.Equals(message.Role, "assistant", StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(message.ReasoningContent));
        var usage = stepState.AggregatedUsage;
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "finishReason={0} hasStreamedText={1} hasReasoning={2} reasoningOnly={3} round={4}/{5} finalNoToolsStep={6} promptTokens={7} completionTokens={8} totalTokens={9}",
            NormalizeOptional(stepState.LastFinishReason) ?? "(none)",
            stepState.HasStreamedTextContent,
            hasReasoning,
            hasReasoning && !stepState.HasStreamedTextContent,
            stepState.Round,
            stepState.MaxToolRounds,
            stepState.FinalNoToolsStep,
            usage?.PromptTokens ?? 0,
            usage?.CompletionTokens ?? 0,
            usage?.TotalTokens ?? 0);
    }

    private static bool ShouldCompleteAfterLlmStep(AgentRunReplyStepState stepState, bool isCompletedLlmStep)
    {
        if (stepState.PendingToolCalls.Count > 0)
            return false;

        // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
        // slash silently consumed.
        // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
        // non-slash text path unchanged (owner-LLM chat fallback).
        if (stepState.FinalNoToolsStep)
            return isCompletedLlmStep;

        if (isCompletedLlmStep && string.IsNullOrWhiteSpace(stepState.AccumulatedText))
            return true;

        if (stepState.Round >= stepState.MaxToolRounds)
            return false;

        return !string.IsNullOrWhiteSpace(stepState.AccumulatedText);
    }

    private async Task<AgentRunReplyStepState> AdvanceToFinalNoToolsStepAsync(AgentRunReplyStepState stepState)
    {
        var next = stepState.Clone();
        next.FinalNoToolsStep = true;
        next.NextStepIndex++;
        await PersistStepStateAsync(next);
        return next;
    }

    private bool IsCurrentStepRequest(string runId, string correlationId, int attempt, int stepIndex)
    {
        if (!IsCurrentGenerationContinuation(runId, correlationId, attempt))
            return false;

        var currentStep = State.GenerationStep;
        if (currentStep is null)
            return stepIndex <= 1;

        return stepIndex == currentStep.NextStepIndex;
    }

    private bool IsCurrentStepResult(string runId, string correlationId, int attempt, int stepIndex)
    {
        if (!IsCurrentGenerationContinuation(runId, correlationId, attempt))
            return false;

        var currentStep = State.GenerationStep;
        if (currentStep is null)
            return false;

        return stepIndex == currentStep.NextStepIndex + 1;
    }

    private static NeedsLlmReplyEvent BuildStepRequest(AgentRunNextLlmStepRequestedEvent command) =>
        new()
        {
            RunId = command.RunId,
            CorrelationId = command.CorrelationId,
            TargetActorId = command.TargetActorId,
        };

    private static NeedsLlmReplyEvent BuildStepRequest(AgentRunNextToolStepRequestedEvent command) =>
        new()
        {
            RunId = command.RunId,
            CorrelationId = command.CorrelationId,
            TargetActorId = command.TargetActorId,
        };

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleNextLlmStepAsync(AgentRunNextLlmStepRequestedEvent command)
    {
        // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
        //   Old pattern: AgentRunReplyGenerationExecutor performs LLM/tool IO and constructs the authoritative next AgentRunReplyStepState outside the run actor.
        //   New principle: Executor returns typed IO facts only; AgentRunGAgent applies deterministic step-state transition and persists state inside actor event handling.
        ArgumentNullException.ThrowIfNull(command);
        var hasResult = command.LlmStepResult is not null;
        if (hasResult)
        {
            if (!IsCurrentStepResult(command.RunId, command.CorrelationId, command.Attempt, command.StepIndex))
                return;

            await PersistStepStateAsync(ApplyLlmStepResult(State.GenerationStep!, command.LlmStepResult!, command.StepIndex));
        }
        else if (!IsCurrentStepRequest(command.RunId, command.CorrelationId, command.Attempt, command.StepIndex))
        {
            return;
        }

        var request = command.Request?.Clone() ?? BuildStepRequest(command);
        ApplyTargetRefOverrides(request);

        var stepState = State.GenerationStep;
        if (stepState is null)
            return;

        if (ShouldCompleteAfterLlmStep(stepState, hasResult))
        {
            await CompletePerStepReplyAsync(request, stepState);
            return;
        }

        if (stepState.PendingToolCalls.Count > 0)
        {
            await DispatchToolStepExecutorAsync(request, stepState);
            return;
        }

        if (stepState.Round >= stepState.MaxToolRounds && !stepState.FinalNoToolsStep)
            stepState = await AdvanceToFinalNoToolsStepAsync(stepState);

        await DispatchLlmStepExecutorAsync(request, stepState);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleNextToolStepAsync(AgentRunNextToolStepRequestedEvent command)
    {
        // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
        //   Old pattern: AgentRunReplyGenerationExecutor performs LLM/tool IO and constructs the authoritative next AgentRunReplyStepState outside the run actor.
        //   New principle: Executor returns typed IO facts only; AgentRunGAgent applies deterministic step-state transition and persists state inside actor event handling.
        ArgumentNullException.ThrowIfNull(command);
        var hasResult = command.ToolStepResult is not null;
        if (hasResult)
        {
            if (!IsCurrentStepResult(command.RunId, command.CorrelationId, command.Attempt, command.StepIndex))
                return;

            await PersistStepStateAsync(ApplyToolStepResult(State.GenerationStep!, command.ToolStepResult!, command.StepIndex));
        }
        else if (!IsCurrentStepRequest(command.RunId, command.CorrelationId, command.Attempt, command.StepIndex))
        {
            return;
        }

        var request = command.Request?.Clone() ?? BuildStepRequest(command);
        ApplyTargetRefOverrides(request);
        var stepState = State.GenerationStep;
        if (stepState is null)
            return;

        if (stepState.Round >= stepState.MaxToolRounds && !stepState.FinalNoToolsStep)
            stepState = await AdvanceToFinalNoToolsStepAsync(stepState);

        await DispatchLlmStepExecutorAsync(request, stepState);
    }

    // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
    //   Old pattern: AgentRunReplyGenerationExecutor cloned/mutated AgentRunReplyStepState and the actor persisted that full state.
    //   New principle: Executor returns typed IO facts; actor applies deterministic step-state transition inside event handling.
    private static AgentRunReplyStepState ApplyLlmStepResult(
        AgentRunReplyStepState current,
        AgentRunLlmStepResult result,
        int completedStepIndex)
    {
        var next = current.Clone();
        next.NextStepIndex = completedStepIndex;
        next.AccumulatedText = result.AccumulatedText ?? string.Empty;
        AddUsage(next, result.Usage);
        if (result.OutboundIntent is not null)
            next.OutboundIntent = result.OutboundIntent.Clone();
        if (!string.IsNullOrEmpty(result.FinishReason))
            next.LastFinishReason = result.FinishReason;
        if (result.HasStreamedTextContent)
            next.HasStreamedTextContent = true;

        next.PendingToolCalls.Clear();
        if (result.ToolCalls.Count > 0)
            next.PendingToolCalls.AddRange(result.ToolCalls.Select(call => call.Clone()));

        if (!string.IsNullOrEmpty(result.Content) ||
            !string.IsNullOrEmpty(result.ReasoningContent) ||
            result.ToolCalls.Count > 0)
        {
            var message = new AgentRunChatMessage
            {
                Role = "assistant",
                Content = result.Content ?? string.Empty,
                ReasoningContent = result.ReasoningContent ?? string.Empty,
            };
            message.ToolCalls.AddRange(result.ToolCalls.Select(call => call.Clone()));
            next.Messages.Add(message);
            next.AppendedHistory.Add(AgentRunReplyStepMappers.ToConversationHistoryEntry(message));
        }

        return next;
    }

    // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
    //   Old pattern: AgentRunReplyGenerationExecutor cloned/mutated AgentRunReplyStepState and the actor persisted that full state.
    //   New principle: Executor returns typed IO facts; actor applies deterministic step-state transition inside event handling.
    private static AgentRunReplyStepState ApplyToolStepResult(
        AgentRunReplyStepState current,
        AgentRunToolStepResult result,
        int completedStepIndex)
    {
        var next = current.Clone();
        next.NextStepIndex = completedStepIndex;
        next.PendingToolCalls.Clear();
        next.Messages.AddRange(result.ResultMessages.Select(message => message.Clone()));
        next.AppendedHistory.AddRange(
            result.ResultMessages.Select(AgentRunReplyStepMappers.ToConversationHistoryEntry));
        next.ToolReceipts.AddRange(result.ToolReceipts.Select(receipt => receipt.Clone()));
        if (result.OutboundIntent is not null)
            next.OutboundIntent = result.OutboundIntent.Clone();
        if (result.AdvanceRound)
            next.Round++;
        return next;
    }

    // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
    //   Old pattern: AgentRunReplyGenerationExecutor cloned/mutated AgentRunReplyStepState and the actor persisted that full state.
    //   New principle: Executor returns typed IO facts; actor applies deterministic step-state transition inside event handling.
    private static void AddUsage(AgentRunReplyStepState state, AgentRunReplyTokenUsage? usage)
    {
        if (usage is null)
            return;

        state.AggregatedUsage ??= new AgentRunReplyTokenUsage();
        state.AggregatedUsage.PromptTokens += usage.PromptTokens;
        state.AggregatedUsage.CompletionTokens += usage.CompletionTokens;
        state.AggregatedUsage.TotalTokens += usage.TotalTokens;
    }

    /// <summary>
    /// Persists the immutable produced reply payload BEFORE attempting to dispatch the
    /// LlmReplyReadyEvent to the conversation actor. If dispatch then fails, the
    /// output-dispatch retry path replays from state via
    /// <see cref="ReDispatchProducedReplyAsync"/> instead of re-running the LLM /
    /// tool chain — which would otherwise repeat side effects (SSH exec, external API
    /// calls, billing) and could surface a different reply than the persisted one.
    /// </summary>
    private async Task ProduceAndDispatchAsync(
        NeedsLlmReplyEvent request,
        string runId,
        string replyText,
        MessageContent? outboundIntent,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary,
        IReadOnlyList<ConversationHistoryEntry>? appendedHistory = null,
        IReadOnlyList<Aevatar.AI.Abstractions.AgentToolReceipt>? toolReceipts = null,
        IReadOnlyList<AgentRunToolCall>? toolCalls = null)
    {
        var renderedReplyText = RenderReplyWithReceipts(replyText, toolReceipts, toolCalls);
        await PersistReplyProducedAsync(
            request,
            runId,
            renderedReplyText,
            outboundIntent,
            terminalState,
            errorCode,
            errorSummary,
            appendedHistory,
            toolReceipts);

        await DispatchReadyEventAsync(
            request,
            runId,
            renderedReplyText,
            outboundIntent,
            terminalState,
            errorCode,
            errorSummary,
            appendedHistory);

        // Past the point of user-visible delivery. State persistence failures and cleanup
        // scheduling failures MUST NOT propagate out — otherwise HandleStartAsync's outer
        // `catch (Exception)` would call FailAfterUnexpectedExceptionAsync, which would
        // re-enter ProduceAndDispatchAsync with a fallback reply and deliver a SECOND
        // user-visible message ("Sorry, I couldn't complete this reply..."). Log and
        // continue; the actor stays at Status=ReplyProduced && !ReplyDispatched, and the
        // terminal cleanup callback simply doesn't fire (actor lingers until normal
        // grain idle eviction). The conversation actor has already accepted the reply.
        await TryFinalizeAfterDispatchAsync(request, runId);
    }

    /// <summary>
    /// Output-dispatch retry path: re-deliver the produced payload from state without
    /// re-running the LLM. Triggered when <see cref="HandleStartAsync"/> sees
    /// <c>State.Status == ReplyProduced</c> (committed but not yet handed off).
    /// </summary>
    private async Task ReDispatchProducedReplyAsync(NeedsLlmReplyEvent request, string runId)
    {
        var outbound = State.ProducedOutbound;
        await DispatchReadyEventAsync(
            request,
            runId,
            State.ProducedReplyText ?? string.Empty,
            outbound,
            State.ProducedTerminalState,
            State.ErrorCode ?? string.Empty,
            State.ErrorSummary ?? string.Empty,
            State.ProducedAppendedHistory.ToArray());

        // Past the point of user-visible delivery — swallow persistence/cleanup errors so
        // they don't escalate to a duplicate fallback dispatch. See ProduceAndDispatchAsync
        // for the full rationale.
        await TryFinalizeAfterDispatchAsync(request, runId);
    }

    /// <summary>
    /// Post-dispatch state finalization. Once <see cref="DispatchReadyEventAsync"/> has
    /// succeeded the user has the reply, so any state-persistence or cleanup-scheduling
    /// failure from here on must NOT bubble up — otherwise the outer exception path
    /// would treat this as an unhandled failure and re-dispatch a fallback reply,
    /// surfacing a duplicate message to the user.
    /// </summary>
    private async Task TryFinalizeAfterDispatchAsync(NeedsLlmReplyEvent request, string runId)
    {
        try
        {
            await PersistReplyDispatchedAsync(request, runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to persist AgentRunReplyDispatchedEvent after successful dispatch; " +
                "state will replay as ReplyProduced+!ReplyDispatched until next reconciliation. " +
                "runId={RunId} correlation={CorrelationId}",
                runId,
                request.CorrelationId);
        }

        try
        {
            await ScheduleTerminalCleanupAsync(runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to schedule terminal cleanup after successful dispatch; actor may " +
                "linger until normal grain idle eviction. runId={RunId} correlation={CorrelationId}",
                runId,
                request.CorrelationId);
        }
    }

    private async Task DropAsync(NeedsLlmReplyEvent request, string runId, string reason)
    {
        // Refactor (iter99/cluster-605-drop-outbox):
        //   Old pattern: notify DeferredLlmReplyDroppedEvent before persisting AgentRunDroppedEvent; notification failure left no actor-owned retry fact.
        //   New principle: persist the drop outbox fact first, then notify; retry callbacks replay the notification from AgentRunGAgentState.
        var dropped = new AgentRunDroppedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            Reason = reason,
            DroppedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };
        await PersistDomainEventAsync(dropped);

        if (CanNotifyDrop(request))
        {
            try
            {
                await DispatchPendingDropNotificationAsync();
                await TryPersistDropNotificationDispatchedAsync();
            }
            catch (AgentRunOutputDispatchException ex)
            {
                await TryScheduleDropNotificationRetryAsync(1, ex);
            }
        }

        await ScheduleTerminalCleanupAsync(runId);
    }

    private async Task PersistReplyProducedAsync(
        NeedsLlmReplyEvent request,
        string runId,
        string replyText,
        MessageContent? outbound,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary,
        IReadOnlyList<ConversationHistoryEntry>? appendedHistory,
        IReadOnlyList<Aevatar.AI.Abstractions.AgentToolReceipt>? toolReceipts)
    {
        var evt = new AgentRunReplyProducedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            TerminalState = terminalState,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            ProducedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ReplyText = replyText ?? string.Empty,
        };
        if (outbound is not null)
            evt.Outbound = outbound.Clone();
        evt.AppendedHistory.AddRange((appendedHistory ?? []).Select(entry => entry.Clone()));
        evt.ToolReceipts.AddRange((toolReceipts ?? []).Select(receipt => receipt.Clone()));
        await PersistDomainEventAsync(evt);
    }

    private string RenderReplyWithReceipts(
        string replyText,
        IReadOnlyList<Aevatar.AI.Abstractions.AgentToolReceipt>? toolReceipts,
        IReadOnlyList<AgentRunToolCall>? toolCalls)
    {
        if (toolReceipts is not { Count: > 0 })
            return replyText ?? string.Empty;

        var rendered = _toolReceiptRenderer.Render(toolReceipts, toolCalls ?? []);
        if (string.IsNullOrWhiteSpace(rendered))
            return replyText ?? string.Empty;

        if (string.IsNullOrWhiteSpace(replyText))
            return rendered;

        return $"{replyText.TrimEnd()}\n\n{rendered}";
    }

    private async Task PersistReplyDispatchedAsync(NeedsLlmReplyEvent request, string runId)
    {
        await PersistDomainEventAsync(new AgentRunReplyDispatchedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            DispatchedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private async Task TryPersistDropNotificationDispatchedAsync()
    {
        try
        {
            await PersistDomainEventAsync(new AgentRunDropNotificationDispatchedEvent
            {
                RunId = State.PendingDropNotificationRunId,
                CorrelationId = State.PendingDropNotificationCorrelationId,
                TargetActorId = State.PendingDropNotificationTargetActorId,
                DispatchedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist AgentRunDropNotificationDispatchedEvent after successful drop notification; " +
                "drop outbox may retry until the dispatched marker is committed. runId={RunId} correlation={CorrelationId}",
                State.PendingDropNotificationRunId,
                State.PendingDropNotificationCorrelationId);
        }
    }

    private async Task PersistFailedAsync(
        NeedsLlmReplyEvent request,
        string runId,
        string errorCode,
        string errorSummary)
    {
        await PersistDomainEventAsync(new AgentRunFailedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await ScheduleTerminalCleanupAsync(runId);
    }

    private async Task FailAfterUnexpectedExceptionAsync(NeedsLlmReplyEvent request, string runId, Exception ex)
    {
        const string errorCode = "agent_run_unhandled_exception";
        var errorSummary = ex.Message;
        _logger.LogError(
            ex,
            "Agent run failed with unhandled exception: runId={RunId} correlation={CorrelationId}",
            runId,
            request.CorrelationId);

        if (request.Activity is null || string.IsNullOrWhiteSpace(request.TargetActorId))
        {
            // Cannot dispatch a fallback reply at all; terminate the run as Failed so the
            // state is not left stuck in Started.
            await PersistFailedAsync(request, runId, errorCode, errorSummary);
            return;
        }

        // Persist the fallback reply BEFORE dispatching so a dispatch retry replays from
        // state rather than re-entering ProcessAsync (which would just throw again). If
        // dispatch itself fails and we cannot schedule a retry, fall through to a Failed
        // terminal marker with the dispatch error appended to errorSummary (carried over
        // from feature/lark-bot's dispatch hardening).
        try
        {
            await ProduceAndDispatchAsync(
                request,
                runId,
                "Sorry, I couldn't complete this reply. Please try again.",
                null,
                LlmReplyTerminalState.Failed,
                errorCode,
                errorSummary);
        }
        catch (AgentRunOutputDispatchException dispatchEx)
        {
            if (await TryHandleOutputDispatchFailureAsync(request, runId, dispatchEx))
                return;

            errorSummary = $"{errorSummary}; failed to dispatch failure notification: {dispatchEx.Message}";
            await PersistFailedAsync(request, runId, errorCode, errorSummary);
        }
    }

    private async Task DispatchReadyEventAsync(
        NeedsLlmReplyEvent request,
        string runId,
        string replyText,
        MessageContent? outboundIntent,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary,
        IReadOnlyList<ConversationHistoryEntry>? appendedHistory = null)
    {
        if (string.IsNullOrWhiteSpace(request.TargetActorId))
            return;

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = request.CorrelationId,
            RegistrationId = request.RegistrationId,
            SourceActorId = Id,
            Activity = request.Activity!.Clone(),
            Outbound = outboundIntent?.Clone() ?? new MessageContent { Text = replyText },
            TerminalState = terminalState,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            ReadyAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            // Echo the command-only relay credential straight back so ConversationGAgent's
            // outbound reply does not depend on its in-memory token dict still having the
            // entry. The actor consumes these fields and never persists them.
            ReplyToken = request.ReplyToken ?? string.Empty,
            ReplyTokenExpiresAtUnixMs = request.ReplyTokenExpiresAtUnixMs,
            RunId = runId,
        };
        ready.AppendedHistory.AddRange((appendedHistory ?? []).Select(entry => entry.Clone()));
        try
        {
            await SendToAsync(request.TargetActorId, ready, CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new AgentRunOutputDispatchException(
                $"Failed to send LLM reply ready event to conversation actor '{request.TargetActorId}'.",
                ex);
        }
    }

    private TimeSpan ResolveFallbackTimeout()
    {
        if (_relayOptions is null)
            return TimeSpan.FromSeconds(FallbackTimeoutSecondsDefault);
        var configured = _relayOptions.ResponseTimeoutSeconds;
        if (configured <= 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(configured);
    }

    private static bool IsRelayRequest(NeedsLlmReplyEvent request) =>
        request.Activity?.OutboundDelivery is
        {
            ReplyMessageId.Length: > 0,
            CorrelationId.Length: > 0,
        };

    private static bool CanNotifyDrop(NeedsLlmReplyEvent request) =>
        !string.IsNullOrWhiteSpace(request.TargetActorId) &&
        !string.IsNullOrWhiteSpace(request.CorrelationId);

    private async Task DispatchPendingDropNotificationAsync()
    {
        var targetActorId = NormalizeOptional(State.PendingDropNotificationTargetActorId);
        var correlationId = NormalizeOptional(State.PendingDropNotificationCorrelationId);
        if (targetActorId is null || correlationId is null)
            return;

        var dropped = new DeferredLlmReplyDroppedEvent
        {
            CorrelationId = correlationId,
            Reason = State.PendingDropNotificationReason ?? string.Empty,
            DroppedAtUnixMs = State.PendingDropNotificationDroppedAtUnixMs > 0
                ? State.PendingDropNotificationDroppedAtUnixMs
                : _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };

        try
        {
            await SendToAsync(targetActorId, dropped, CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new AgentRunOutputDispatchException(
                $"Failed to send deferred LLM reply drop event to conversation actor '{targetActorId}' (reason '{dropped.Reason}').",
                ex);
        }
    }

    private async Task DispatchGenerationTimeoutDropNotificationAsync(AgentRunReplyGenerationTimedOut command)
    {
        if (string.IsNullOrWhiteSpace(command.TargetActorId) ||
            string.IsNullOrWhiteSpace(command.CorrelationId))
        {
            return;
        }

        var dropped = new DeferredLlmReplyDroppedEvent
        {
            CorrelationId = command.CorrelationId,
            Reason = "llm_reply_timeout",
            DroppedAtUnixMs = command.TimedOutAtUnixMs > 0
                ? command.TimedOutAtUnixMs
                : _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };

        try
        {
            await SendToAsync(command.TargetActorId, dropped, CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new AgentRunOutputDispatchException(
                $"Failed to send agent run generation timeout drop event to conversation actor '{command.TargetActorId}'.",
                ex);
        }
    }

    private async Task<bool> TryHandleOutputDispatchFailureAsync(
        NeedsLlmReplyEvent request,
        string runId,
        AgentRunOutputDispatchException ex)
    {
        _logger.LogWarning(
            ex,
            "Agent run output notification was not accepted; run remains retryable: runId={RunId} correlation={CorrelationId}",
            runId,
            request.CorrelationId);

        if (await TryScheduleStartRetryAsync(request, runId))
            return true;

        _logger.LogWarning(
            ex,
            "Agent run output retry could not be scheduled; persisting terminal failure: runId={RunId} correlation={CorrelationId}",
            runId,
            request.CorrelationId);
        return false;
    }

    private async Task<bool> TryScheduleStartRetryAsync(NeedsLlmReplyEvent request, string runId)
    {
        if (_callbackScheduler is null)
            return false;

        try
        {
            await _callbackScheduler.ScheduleTimeoutAsync(
                BuildTimeoutRequest(
                    BuildOutputDispatchRetryCallbackId(runId),
                    OutputDispatchRetryDelay,
                    new AgentRunOutputDispatchRetryRequested
                    {
                        RunId = runId,
                        CorrelationId = request.CorrelationId,
                        TargetActorId = request.TargetActorId,
                        Attempt = State.GenerationAttempt,
                        Generation = Math.Max(1, State.GenerationAttempt),
                        RequestedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                        RequiresRuntimeReplyToken = IsRelayRequest(request),
                    }),
                ct: CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to schedule agent run output retry: runId={RunId} actorId={ActorId}",
                runId,
                Id);
            return false;
        }
    }

    private async Task<bool> TryScheduleDropNotificationRetryAsync(
        int attempt,
        AgentRunOutputDispatchException dispatchException)
    {
        _logger.LogWarning(
            dispatchException,
            "Agent run drop notification was not accepted; run remains retryable from drop outbox state: runId={RunId} correlation={CorrelationId}",
            State.PendingDropNotificationRunId,
            State.PendingDropNotificationCorrelationId);

        if (_callbackScheduler is null)
            return false;

        try
        {
            await _callbackScheduler.ScheduleTimeoutAsync(
                BuildTimeoutRequest(
                    BuildDropNotificationRetryCallbackId(State.PendingDropNotificationRunId, attempt),
                    DropNotificationRetryDelay,
                    new AgentRunDropNotificationRetryRequested
                    {
                        RunId = State.PendingDropNotificationRunId,
                        CorrelationId = State.PendingDropNotificationCorrelationId,
                        TargetActorId = State.PendingDropNotificationTargetActorId,
                        Attempt = Math.Max(1, attempt),
                        RequestedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    }),
                ct: CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to schedule agent run drop notification retry: runId={RunId} actorId={ActorId}",
                State.PendingDropNotificationRunId,
                Id);
            return false;
        }
    }

    private async Task ScheduleTerminalCleanupAsync(string runId)
    {
        if (_callbackScheduler is null)
            return;

        try
        {
            await _callbackScheduler.ScheduleTimeoutAsync(
                BuildTimeoutRequest(
                    BuildCleanupCallbackId(runId),
                    TerminalCleanupDelay,
                    new AgentRunCleanupRequested
                    {
                        RunId = runId,
                        RequestedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    }),
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to schedule terminal agent run cleanup: runId={RunId} actorId={ActorId}",
                runId,
                Id);
        }
    }

    private async Task ScheduleGenerationTimeoutAsync(NeedsLlmReplyEvent request, string runId, int attempt)
    {
        if (_callbackScheduler is null)
            return;

        var fallbackTimeout = ResolveFallbackTimeout();
        if (fallbackTimeout <= TimeSpan.Zero)
            return;

        try
        {
            await _callbackScheduler.ScheduleTimeoutAsync(
                BuildTimeoutRequest(
                    BuildGenerationTimeoutCallbackId(runId, attempt),
                    fallbackTimeout,
                    new AgentRunReplyGenerationTimedOut
                    {
                        RunId = runId,
                        CorrelationId = request.CorrelationId,
                        TargetActorId = request.TargetActorId,
                        TimedOutAtUnixMs = _timeProvider.GetUtcNow().Add(fallbackTimeout).ToUnixTimeMilliseconds(),
                        Attempt = attempt,
                    }),
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to schedule agent run generation timeout: runId={RunId} actorId={ActorId}",
                runId,
                Id);
        }
    }

    private bool IsCurrentOutputDispatchRetry(AgentRunOutputDispatchRetryRequested command)
    {
        if (IsTerminal())
            return false;

        if (State.Status is not AgentRunStatus.ReplyProduced)
            return false;

        if (!string.IsNullOrWhiteSpace(State.RunId) &&
            !string.Equals(State.RunId, command.RunId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(State.CorrelationId) &&
            !string.Equals(State.CorrelationId, command.CorrelationId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(State.TargetActorId) &&
            !string.Equals(State.TargetActorId, command.TargetActorId, StringComparison.Ordinal))
        {
            return false;
        }

        if (command.Generation > 0 && State.GenerationAttempt != command.Generation)
            return false;

        return command.Attempt <= 0 || State.GenerationAttempt == command.Attempt;
    }

    private bool IsCurrentDropNotificationRetry(AgentRunDropNotificationRetryRequested command)
    {
        if (State.Status is not AgentRunStatus.Dropped)
            return false;

        if (State.DropNotificationDispatchedAtUnixMs != 0)
            return false;

        if (string.IsNullOrWhiteSpace(State.PendingDropNotificationCorrelationId) ||
            string.IsNullOrWhiteSpace(State.PendingDropNotificationTargetActorId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(State.PendingDropNotificationRunId) &&
            !string.Equals(State.PendingDropNotificationRunId, command.RunId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(State.PendingDropNotificationCorrelationId) &&
            !string.Equals(State.PendingDropNotificationCorrelationId, command.CorrelationId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(State.PendingDropNotificationTargetActorId) &&
            !string.Equals(State.PendingDropNotificationTargetActorId, command.TargetActorId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private NeedsLlmReplyEvent BuildOutputDispatchRetryRequest(AgentRunOutputDispatchRetryRequested command) =>
        new()
        {
            RunId = command.RunId ?? string.Empty,
            CorrelationId = NormalizeOptional(command.CorrelationId) ?? State.CorrelationId ?? string.Empty,
            TargetActorId = NormalizeOptional(command.TargetActorId) ?? State.TargetActorId ?? string.Empty,
            Activity = BuildOutputDispatchRetryActivity(command),
        };

    private ChatActivity? BuildOutputDispatchRetryActivity(AgentRunOutputDispatchRetryRequested command)
    {
        var correlationId = NormalizeOptional(command.CorrelationId) ?? NormalizeOptional(State.CorrelationId);
        if (correlationId is null)
            return null;

        return new ChatActivity
        {
            Id = correlationId,
            OutboundDelivery = command.RequiresRuntimeReplyToken
                ? new OutboundDeliveryContext
                {
                    CorrelationId = correlationId,
                    ReplyMessageId = correlationId,
                }
                : null,
        };
    }

    private RuntimeCallbackTimeoutRequest BuildTimeoutRequest(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt)
    {
        return new RuntimeCallbackTimeoutRequest
        {
            ActorId = Id,
            CallbackId = callbackId,
            TriggerEnvelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                Payload = Any.Pack(evt),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(Id, TopologyAudience.Self),
            },
            DueTime = dueTime,
        };
    }

    private static string BuildCleanupCallbackId(string runId)
    {
        var normalized = NormalizeOptional(runId) ?? "unknown";
        var chars = normalized
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .Take(96)
            .ToArray();
        return $"{TerminalCleanupCallbackPrefix}:{new string(chars)}";
    }

    private static string BuildOutputDispatchRetryCallbackId(string runId)
    {
        var normalized = NormalizeOptional(runId) ?? "unknown";
        var chars = normalized
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .Take(96)
            .ToArray();
        return $"{OutputDispatchRetryCallbackPrefix}:{new string(chars)}";
    }

    private static string BuildDropNotificationRetryCallbackId(string runId, int attempt)
    {
        var normalized = NormalizeOptional(runId) ?? "unknown";
        var chars = normalized
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .Take(96)
            .ToArray();
        return $"{DropNotificationRetryCallbackPrefix}:{new string(chars)}:{Math.Max(1, attempt)}";
    }

    private static string BuildGenerationTimeoutCallbackId(string runId, int attempt)
    {
        var normalized = NormalizeOptional(runId) ?? "unknown";
        var chars = normalized
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .Take(96)
            .ToArray();
        return $"{GenerationTimeoutCallbackPrefix}:{new string(chars)}:{attempt}";
    }

    private async Task EnsureTargetActorAsync(string targetActorId)
    {
        if (string.IsNullOrWhiteSpace(targetActorId))
            return;

        var actor = await _actorRuntime.GetAsync(targetActorId);
        if (actor is null)
            await _actorRuntime.CreateAsync<ConversationGAgent>(targetActorId, CancellationToken.None);
    }

    private bool IsCurrentGenerationContinuation(string runId, string correlationId, int attempt)
    {
        if (IsTerminal())
            return false;

        if (State.Status is not AgentRunStatus.ReplyGenerationRequested)
            return false;

        if (!string.IsNullOrWhiteSpace(State.RunId) &&
            !string.Equals(State.RunId, runId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(State.CorrelationId) &&
            !string.Equals(State.CorrelationId, correlationId, StringComparison.Ordinal))
        {
            return false;
        }

        return attempt <= 0 || State.GenerationAttempt == attempt;
    }

    /// <summary>
    /// Applies the chat-route boundary decision carried on
    /// <see cref="NeedsLlmReplyEvent.TargetRef"/> to the cloned request before
    /// it flows into <c>ProcessAsync</c>. Without this the typed field is
    /// written by <c>ConversationGAgent</c> + persisted but no downstream
    /// consumer ever reads it — Forward* actions silently no-op on the relay
    /// path while only <c>Reject</c> is honored at the resolver edge.
    ///
    /// Action semantics on the relay run-actor path:
    /// <list type="bullet">
    ///   <item><c>ForwardToModel.tool_choice_hint</c> is preserved as tool
    ///     prefill only; it never overrides <see cref="NeedsLlmReplyEvent.TargetActorId"/>.</item>
    ///   <item><c>ForwardToModel.model_name</c> is mapped by the generation executor
    ///     this typed route decision into LLM metadata before invoking the
    ///     provider.</item>
    ///   <item><c>Reject</c> means resolver-side already failed the turn before
    ///     the run was dispatched; this method shouldn't see it.</item>
    ///   <item>Anything else / no TargetRef is a no-op (resolver returned
    ///     fallback / no policy / unsupported v2 action).</item>
    /// </list>
    /// </summary>
    private void ApplyTargetRefOverrides(NeedsLlmReplyEvent request)
    {
        var targetRef = request.TargetRef;
        if (targetRef is null)
            return;

        switch (targetRef.ActionCase)
        {
            case ChatRouteAction.ActionOneofCase.ForwardToModel:
                // Refactor (issue1321-first): ForwardToModel.tool_choice_hint is tool prefill,
                // not actor addressing. The run target stays owned by the dispatch command.
                var routedModel = NormalizeOptional(targetRef.ForwardToModel?.ModelName);
                if (routedModel is not null)
                {
                    _logger.LogInformation(
                        "Chat-route override: pinning LLM model to {Model} (correlation={CorrelationId})",
                        routedModel,
                        request.CorrelationId);
                }
                break;
            default:
                // ForwardToWorkflow is v2 (no relay-side implementation);
                // Reject was handled at the resolver before run dispatch;
                // None means resolver returned no rule + no default.
                break;
        }
    }

    private static AgentRunGAgentState ApplyStarted(AgentRunGAgentState current, AgentRunStartedEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId;
        next.CorrelationId = evt.CorrelationId;
        next.TargetActorId = evt.TargetActorId;
        next.Status = AgentRunStatus.Started;
        next.StartedAtUnixMs = evt.StartedAtUnixMs;
        return next;
    }

    private static AgentRunGAgentState ApplyReplyGenerationRequested(
        AgentRunGAgentState current,
        AgentRunReplyGenerationRequestedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        next.Status = AgentRunStatus.ReplyGenerationRequested;
        next.GenerationRequestedAtUnixMs = evt.RequestedAtUnixMs;
        next.GenerationAttempt = evt.Attempt;
        return next;
    }

    private static AgentRunGAgentState ApplyReplyStepStateUpdated(
        AgentRunGAgentState current,
        AgentRunReplyStepStateUpdatedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        if (evt.Attempt > 0)
            next.GenerationAttempt = evt.Attempt;
        next.GenerationStep = evt.StepState?.Clone();
        return next;
    }

    private static AgentRunGAgentState ApplyReplyProduced(
        AgentRunGAgentState current,
        AgentRunReplyProducedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        next.Status = AgentRunStatus.ReplyProduced;
        next.CompletedAtUnixMs = evt.ProducedAtUnixMs;
        next.ErrorCode = evt.ErrorCode;
        next.ErrorSummary = evt.ErrorSummary;
        next.ProducedReplyText = evt.ReplyText ?? string.Empty;
        next.ProducedOutbound = evt.Outbound?.Clone();
        next.ProducedTerminalState = evt.TerminalState;
        next.ProducedAppendedHistory.Clear();
        next.ProducedAppendedHistory.AddRange(evt.AppendedHistory.Select(entry => entry.Clone()));
        next.ToolReceipts.Clear();
        next.ToolReceipts.AddRange(evt.ToolReceipts.Select(receipt => receipt.Clone()));
        // Backward-compat: AgentRunReplyProducedEvents persisted by the pre-refactor
        // codepath have no reply_text / outbound / terminal_state fields (proto3 defaults
        // on deserialize). Historically, Status=ReplyProduced was only written *after* the
        // LlmReplyReadyEvent was successfully dispatched (old code's `await Dispatch...;
        // await PersistReplyProduced...;` order), so those events semantically mean
        // "handed off". Promote them straight to REPLY_HANDED_OFF on replay so:
        //   1. ReDispatchProducedReplyAsync doesn't fire with an empty payload
        //      (would surface as a blank reply / structural error to the user).
        //   2. HandleCleanupAsync recognizes them as terminal so the actor can be destroyed.
        //
        // Discriminator: legacy events have BOTH an empty reply_text AND a null outbound.
        // The empty-text-alone check is not enough — interactive-only turns
        // (reply_with_interaction etc.) legitimately produce empty reply_text + non-null
        // outbound (card / button intent). Misclassifying those as "historical" would skip
        // the dispatch retry on failure and silently drop the user's interactive reply.
        if (string.IsNullOrEmpty(evt.ReplyText) && evt.Outbound is null)
            next.Status = AgentRunStatus.ReplyHandedOff;
        // For new events, Status stays at REPLY_PRODUCED here; promoted to REPLY_HANDED_OFF
        // by ApplyReplyDispatched once the LlmReplyReadyEvent is accepted by the
        // conversation actor (see ADR-0021).
        return next;
    }

    private static AgentRunGAgentState ApplyReplyDispatched(
        AgentRunGAgentState current,
        AgentRunReplyDispatchedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        // Promote committed -> handed-off (ADR-0021 AgentRunGAgent-side terminal).
        next.Status = AgentRunStatus.ReplyHandedOff;
        return next;
    }

    private static AgentRunGAgentState ApplyDropped(AgentRunGAgentState current, AgentRunDroppedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        next.Status = AgentRunStatus.Dropped;
        next.CompletedAtUnixMs = evt.DroppedAtUnixMs;
        next.ErrorCode = evt.Reason;
        next.ErrorSummary = string.Empty;
        next.PendingDropNotificationRunId = evt.RunId;
        next.PendingDropNotificationCorrelationId = evt.CorrelationId;
        next.PendingDropNotificationTargetActorId = evt.TargetActorId;
        next.PendingDropNotificationReason = evt.Reason;
        next.PendingDropNotificationDroppedAtUnixMs = evt.DroppedAtUnixMs;
        next.DropNotificationDispatchedAtUnixMs = 0;
        return next;
    }

    private static AgentRunGAgentState ApplyDropNotificationDispatched(
        AgentRunGAgentState current,
        AgentRunDropNotificationDispatchedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        next.DropNotificationDispatchedAtUnixMs = evt.DispatchedAtUnixMs;
        return next;
    }

    private static AgentRunGAgentState ApplyFailed(AgentRunGAgentState current, AgentRunFailedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        next.Status = AgentRunStatus.Failed;
        next.CompletedAtUnixMs = evt.FailedAtUnixMs;
        next.ErrorCode = evt.ErrorCode;
        next.ErrorSummary = evt.ErrorSummary;
        return next;
    }

    // ADR-0021 §6 / canon §9 — combined with a terminal AgentRunStatus, a non-zero
    // cleanup_completed_at_unix_ms is the chain.finalized observable. Late cleanup
    // callbacks short-circuit on this field so duplicates do not re-destroy the actor.
    private static AgentRunGAgentState ApplyCleanupCompleted(
        AgentRunGAgentState current,
        AgentRunCleanupCompletedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.CleanupCompletedAtUnixMs = evt.CompletedAtUnixMs;
        return next;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string ResolveRunId(NeedsLlmReplyEvent request) =>
        AgentRunId.Parse(request.RunId).Value;

    private sealed class AgentRunOutputDispatchException(string message, Exception innerException)
        : Exception(message, innerException);
}
