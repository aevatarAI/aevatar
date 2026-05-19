using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Run-scoped continuation owner for one deferred channel LLM reply.
/// </summary>
public sealed class AgentRunGAgent : GAgentBase<AgentRunGAgentState>
{
    public const string ActorIdPrefix = "channel-agent-run:";

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
    internal static readonly TimeSpan OutputDispatchTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan OutputDispatchRetryDelay = TimeSpan.FromSeconds(5);
    private const string OutputDispatchRetryCallbackPrefix = "agent-run-output-dispatch-retry";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IConversationReplyGenerator _replyGenerator;
    private readonly IInteractiveReplyCollector? _interactiveReplyCollector;
    private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdRelayScopeResolver? _scopeResolver;
    private readonly IUserConfigQueryPort? _userConfigQueryPort;
    private readonly IActorRuntimeCallbackScheduler? _callbackScheduler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRunGAgent> _logger;

    public AgentRunGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? interactiveReplyCollector,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions,
        ILogger<AgentRunGAgent> logger,
        INyxIdRelayScopeResolver? scopeResolver = null,
        IUserConfigQueryPort? userConfigQueryPort = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _interactiveReplyCollector = interactiveReplyCollector;
        _relayOptions = relayOptions;
        _scopeResolver = scopeResolver;
        _userConfigQueryPort = userConfigQueryPort;
        _callbackScheduler = callbackScheduler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static string BuildActorId(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return ActorIdPrefix + correlationId.Trim();
    }

    protected override AgentRunGAgentState TransitionState(AgentRunGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<AgentRunStartedEvent>(ApplyStarted)
            .On<AgentRunReplyProducedEvent>(ApplyReplyProduced)
            .On<AgentRunReplyDispatchedEvent>(ApplyReplyDispatched)
            .On<AgentRunDroppedEvent>(ApplyDropped)
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

        var request = command.Request.Clone();
        var runId = NormalizeOptional(request.CorrelationId) ?? Id;
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
            await ProcessAsync(request, runId);
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

    private async Task ProcessAsync(NeedsLlmReplyEvent request, string runId)
    {
        _logger.LogInformation(
            "Processing agent run LLM reply request: runId={RunId} correlation={CorrelationId} target={TargetActorId}",
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

        string replyText;
        MessageContent? outboundIntent = null;
        var terminalState = LlmReplyTerminalState.Completed;
        var errorCode = string.Empty;
        var errorSummary = string.Empty;
        using TurnStreamingReplySink? streamingSink = TryBuildStreamingSink(request, request.TargetActorId);
        var streamingState = TryBuildStreamingReplyState(streamingSink, request);

        IReadOnlyDictionary<string, string> effectiveMetadata;
        using (var metadataCts = new CancellationTokenSource(MetadataBuildBudget))
        {
            try
            {
                effectiveMetadata = await BuildEffectiveMetadataAsync(request, metadataCts.Token);
            }
            catch (OperationCanceledException ex) when (metadataCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Deferred LLM reply metadata build timed out after {TimeoutSeconds}s: runId={RunId} correlation={CorrelationId}",
                    (int)MetadataBuildBudget.TotalSeconds,
                    runId,
                    request.CorrelationId);
                replyText = "Sorry, I couldn't load your model preferences in time. Please try again.";
                terminalState = LlmReplyTerminalState.Failed;
                errorCode = "llm_reply_metadata_timeout";
                errorSummary = $"Metadata enrichment exceeded {(int)MetadataBuildBudget.TotalSeconds}s budget.";
                await FinalizeFailureStreamingSinkAsync(streamingState, replyText, outboundIntent);
                await ProduceAndDispatchAsync(request, runId, replyText, outboundIntent, terminalState, errorCode, errorSummary);
                return;
            }
        }

        var fallbackTimeout = ResolveFallbackTimeout();
        using var timeoutCts = fallbackTimeout > TimeSpan.Zero
            ? new CancellationTokenSource(fallbackTimeout)
            : new CancellationTokenSource();

        try
        {
            IDisposable? interactiveReplyScope = null;
            try
            {
                if (ShouldCaptureInteractiveReply(request.Activity))
                    interactiveReplyScope = _interactiveReplyCollector?.BeginScope();

                // ADR-0021 §6 / canon §8 actor-edge closeout: the generator returns a
                // single ConversationReplyResult per run carrying aggregated Usage and the
                // last FinishReason. Round-internal terminal markers no longer leak past
                // ChatRuntime, so this is the lone closeout observation point.
                var replyResult = await _replyGenerator.GenerateReplyAsync(
                    request.Activity,
                    effectiveMetadata,
                    streamingState,
                    timeoutCts.Token);
                replyText = replyResult.Text ?? string.Empty;
                if (replyResult.Usage is not null || !string.IsNullOrEmpty(replyResult.FinishReason))
                {
                    _logger.LogInformation(
                        "LLM reply closeout: runId={RunId} correlation={CorrelationId} promptTokens={Prompt} completionTokens={Completion} totalTokens={Total} finishReason={FinishReason}",
                        runId,
                        request.CorrelationId,
                        replyResult.Usage?.PromptTokens,
                        replyResult.Usage?.CompletionTokens,
                        replyResult.Usage?.TotalTokens,
                        replyResult.FinishReason ?? "(none)");
                }
                outboundIntent = _interactiveReplyCollector?.TryTake();
            }
            finally
            {
                interactiveReplyScope?.Dispose();
            }

            if (streamingState is not null &&
                outboundIntent is null &&
                !string.IsNullOrWhiteSpace(replyText))
            {
                await streamingState.FinalizeAsync(replyText, CancellationToken.None);
            }

            if (outboundIntent is null && string.IsNullOrWhiteSpace(replyText))
            {
                terminalState = LlmReplyTerminalState.Failed;
                errorCode = "empty_reply";
                errorSummary = "Reply generator returned an empty response.";
                replyText = "Sorry, I wasn't able to generate a response. Please try again.";
            }
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            terminalState = LlmReplyTerminalState.Failed;
            errorCode = "llm_reply_timeout";
            errorSummary = $"LLM reply generation exceeded {(int)fallbackTimeout.TotalSeconds}s budget.";
            replyText = "Sorry, this took too long to process - the model or one of its tools didn't " +
                        "respond in time. Please try again, or rephrase the request.";
            _logger.LogWarning(
                ex,
                "Deferred LLM reply timed out after {TimeoutSeconds}s: runId={RunId} correlation={CorrelationId}",
                (int)fallbackTimeout.TotalSeconds,
                runId,
                request.CorrelationId);
        }
        catch (Exception ex)
        {
            terminalState = LlmReplyTerminalState.Failed;
            errorCode = "llm_reply_failed";
            errorSummary = ex.Message;
            replyText = NyxIdRelayErrorClassifier.Classify(ex.Message);
            _logger.LogWarning(
                ex,
                "Deferred LLM reply generation failed: runId={RunId} correlation={CorrelationId}",
                runId,
                request.CorrelationId);
        }

        if (terminalState == LlmReplyTerminalState.Failed)
        {
            // Streaming-sink failure finalize: when the LLM run terminates with a fallback
            // text (timeout / classifier / empty reply), surface that text on the live
            // streaming card/edit message before the LlmReplyReadyEvent lands. Carried over
            // from feature/lark-bot's dispatch hardening.
            await FinalizeFailureStreamingSinkAsync(streamingState, replyText, outboundIntent);
        }

        await ProduceAndDispatchAsync(
            request,
            runId,
            replyText,
            outboundIntent,
            terminalState,
            errorCode,
            errorSummary);
    }

    private async Task FinalizeFailureStreamingSinkAsync(
        StreamingReplyRunState? streamingState,
        string replyText,
        MessageContent? outboundIntent)
    {
        if (streamingState is not null &&
            outboundIntent is null &&
            !string.IsNullOrWhiteSpace(replyText))
        {
            try
            {
                await streamingState.FinalizeAsync(replyText, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to finalize streaming failure text for agent run {ActorId}", Id);
            }
        }
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
        string errorSummary)
    {
        await PersistReplyProducedAsync(
            request,
            runId,
            replyText,
            outboundIntent,
            terminalState,
            errorCode,
            errorSummary);

        await DispatchReadyEventAsync(request, replyText, outboundIntent, terminalState, errorCode, errorSummary);

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
            State.ProducedReplyText ?? string.Empty,
            outbound,
            State.ProducedTerminalState,
            State.ErrorCode ?? string.Empty,
            State.ErrorSummary ?? string.Empty);

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
        if (CanNotifyDrop(request))
            await DispatchDropNotificationAsync(request, reason);

        await PersistDomainEventAsync(new AgentRunDroppedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            Reason = reason,
            DroppedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await ScheduleTerminalCleanupAsync(runId);
    }

    private async Task PersistReplyProducedAsync(
        NeedsLlmReplyEvent request,
        string runId,
        string replyText,
        MessageContent? outbound,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary)
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
        await PersistDomainEventAsync(evt);
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
        string replyText,
        MessageContent? outboundIntent,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary)
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
        };
        try
        {
            using var outputCts = new CancellationTokenSource(OutputDispatchTimeout);
            await SendToAsync(request.TargetActorId, ready, outputCts.Token);
        }
        catch (Exception ex)
        {
            throw new AgentRunOutputDispatchException(
                $"Failed to send LLM reply ready event to conversation actor '{request.TargetActorId}'.",
                ex);
        }
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
            _timeProvider,
            _logger,
            cardMode);
    }

    private StreamingReplyRunState? TryBuildStreamingReplyState(TurnStreamingReplySink? sink, NeedsLlmReplyEvent request)
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

    /// <summary>
    /// Actor-owned coalescing state for one generated reply stream.
    /// </summary>
    /// <remarks>
    /// Refactor (iter15/cluster-027-streaming-reply-timer-business-dispatch):
    ///   Old pattern: timer callback directly inspects/mutates pending business output and dispatches actor command from callback thread
    ///   New principle: this run flow owns throttling, duplicate suppression, interim caps, and final flush ordering before dispatch.
    /// </remarks>
    private sealed class StreamingReplyRunState : IStreamingReplySink
    {
        private readonly TurnStreamingReplySink _sink;
        private readonly TimeSpan _throttle;
        private readonly int _maxInterimChunks;
        private readonly TimeProvider _timeProvider;
        private string _lastEmittedText = string.Empty;
        private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
        private int _chunksEmitted;

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
                return;

            if (!isFinal && _chunksEmitted >= _maxInterimChunks)
                return;

            if (!isFinal)
            {
                var elapsed = _timeProvider.GetUtcNow() - _lastEmitAt;
                if (elapsed < _throttle)
                    await Task.Delay(_throttle - elapsed, _timeProvider, ct).ConfigureAwait(false);
            }

            await _sink.DispatchAsync(text, ct).ConfigureAwait(false);
            if (_sink.ChunksEmitted > _chunksEmitted)
            {
                _lastEmittedText = text;
                _lastEmitAt = _timeProvider.GetUtcNow();
                _chunksEmitted = _sink.ChunksEmitted;
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildEffectiveMetadataAsync(
        NeedsLlmReplyEvent request,
        CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);

        await ApplyBotOwnerLlmConfigAsync(request, metadata, ct);

        var userAccessToken = request.Activity?.TransportExtras?.NyxUserAccessToken?.Trim();
        if (!string.IsNullOrWhiteSpace(userAccessToken))
        {
            metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = userAccessToken;
            metadata[LLMRequestMetadataKeys.NyxIdOrgToken] = userAccessToken;
        }

        return metadata;
    }

    private async Task ApplyBotOwnerLlmConfigAsync(
        NeedsLlmReplyEvent request,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        if (_scopeResolver is null || _userConfigQueryPort is null)
            return;

        var apiKeyId = request.Activity?.Bot?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(apiKeyId))
            return;

        string? scopeId;
        try
        {
            scopeId = await _scopeResolver.ResolveScopeIdByApiKeyAsync(apiKeyId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve bot owner scope id for LLM config: runId={RunId} correlation={CorrelationId} apiKeyId={ApiKeyId}",
                Id,
                request.CorrelationId,
                apiKeyId);
            return;
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            _logger.LogDebug(
                "No bot owner scope id resolved for LLM config: runId={RunId} correlation={CorrelationId} apiKeyId={ApiKeyId}",
                Id,
                request.CorrelationId,
                apiKeyId);
            return;
        }

        try
        {
            var config = await _userConfigQueryPort.GetAsync(scopeId, ct);
            if (!string.IsNullOrWhiteSpace(config.DefaultModel))
                metadata[LLMRequestMetadataKeys.ModelOverride] = config.DefaultModel.Trim();
            if (!string.IsNullOrWhiteSpace(config.PreferredLlmRoute))
                metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = config.PreferredLlmRoute.Trim();
            if (config.MaxToolRounds > 0)
                metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride] =
                    config.MaxToolRounds.ToString(System.Globalization.CultureInfo.InvariantCulture);

            _logger.LogInformation(
                "Applied bot owner LLM config: runId={RunId} correlation={CorrelationId} scopeId={ScopeId} model={Model} route={Route}",
                Id,
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
                "Failed to load bot owner LLM config: runId={RunId} correlation={CorrelationId} scopeId={ScopeId}",
                Id,
                request.CorrelationId,
                scopeId);
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

    private async Task DispatchDropNotificationAsync(NeedsLlmReplyEvent request, string reason)
    {
        var dropped = new DeferredLlmReplyDroppedEvent
        {
            CorrelationId = request.CorrelationId,
            Reason = reason,
            DroppedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };

        try
        {
            using var outputCts = new CancellationTokenSource(OutputDispatchTimeout);
            await SendToAsync(request.TargetActorId, dropped, outputCts.Token);
        }
        catch (Exception ex)
        {
            throw new AgentRunOutputDispatchException(
                $"Failed to send deferred LLM reply drop event to conversation actor '{request.TargetActorId}' (reason '{reason}').",
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
                    new AgentRunStartRequested
                    {
                        Request = request.Clone(),
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

    private async Task EnsureTargetActorAsync(string targetActorId)
    {
        if (string.IsNullOrWhiteSpace(targetActorId))
            return;

        var actor = await _actorRuntime.GetAsync(targetActorId);
        if (actor is null)
            await _actorRuntime.CreateAsync<ConversationGAgent>(targetActorId, CancellationToken.None);
    }

    private bool ShouldCaptureInteractiveReply(ChatActivity? activity)
    {
        if (_interactiveReplyCollector is null)
            return false;

        if (_relayOptions is { InteractiveRepliesEnabled: false })
            return false;

        return activity?.OutboundDelivery is
        {
            ReplyMessageId.Length: > 0,
            CorrelationId.Length: > 0,
        };
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

    private sealed class AgentRunOutputDispatchException(string message, Exception innerException)
        : Exception(message, innerException);
}
