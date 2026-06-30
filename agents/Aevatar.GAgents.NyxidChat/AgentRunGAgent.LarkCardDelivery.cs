using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Lark.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

public sealed partial class AgentRunGAgent : IReplyOperationActorContext
{
    private static readonly TimeSpan LarkCardOperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LarkCardTextFallbackOperationTimeout = TimeSpan.FromSeconds(10);

    private sealed record LarkCardOperationInFlight(
        LarkCardOperationPhase Operation,
        long Sequence,
        long Generation);

    private sealed record LarkCardDeliveryRuntimeState(
        AgentRunLarkCardDeliveryPhase Phase,
        string? CardId,
        string? CardMessageId,
        string? OriginalCardId,
        string LastFlushedText,
        long Sequence,
        string StreamingElementId,
        string? TerminalReason,
        LarkCardOperationInFlight? InFlight,
        long OperationGeneration,
        string? PendingAccumulatedText,
        string? PendingFinalizeText,
        string? PendingFinalizeCommandId,
        IReadOnlyList<ConversationHistoryEntry> PendingAppendedHistory)
    {
        public const string DefaultStreamingElementId = "streaming_main";

        public static LarkCardDeliveryRuntimeState Initial { get; } = new(
            AgentRunLarkCardDeliveryPhase.Idle,
            CardId: null,
            CardMessageId: null,
            OriginalCardId: null,
            LastFlushedText: string.Empty,
            Sequence: 0,
            StreamingElementId: DefaultStreamingElementId,
            TerminalReason: null,
            InFlight: null,
            OperationGeneration: 0,
            PendingAccumulatedText: null,
            PendingFinalizeText: null,
            PendingFinalizeCommandId: null,
            PendingAppendedHistory: []);

        public bool AllowsInterimEdit =>
            Phase is AgentRunLarkCardDeliveryPhase.Idle
                  or AgentRunLarkCardDeliveryPhase.Streaming;

        public bool AllowsFinalize =>
            Phase is AgentRunLarkCardDeliveryPhase.Streaming;
    }

    private enum LarkCardDeliveryGuardSource
    {
        AcceptInterimChunk,
        Finalize,
    }

    [EventHandler]
    public async Task HandleLlmReplyCardStreamChunkAsync(LlmReplyCardStreamChunkEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null || evt.Activity is null || string.IsNullOrWhiteSpace(evt.AccumulatedText))
        {
            _logger.LogDebug(
                "Dropping malformed card streaming chunk: runId={RunId} correlation={CorrelationId}",
                State.RunId,
                evt.CorrelationId);
            return;
        }

        if (!IsCurrentCardDeliverySignal(evt.RunId, evt.CorrelationId))
            return;

        if (await HandleLarkCardStreamingChunkCoreAsync(evt, correlationId))
            return;

        if (evt.IsFinal)
        {
            await DeliverLarkCardTextFallbackOnlyAsync(evt, evt.AccumulatedText);
        }
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleReplyOperationStepAsync(ReplyOperationStepEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.PayloadCase != ReplyOperationStepEvent.PayloadOneofCase.LarkCard)
            return;
        if (!string.Equals(NormalizeOptional(evt.CorrelationId), evt.CorrelationId, StringComparison.Ordinal))
            return;

        await ResolveLarkCardReplyStreamRenderer()
            .ExecuteAsync(this, evt, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleLarkCardOperationCompletedAsync(LarkCardOperationCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt.Operation)
        {
            case LarkCardOperationPhase.Create:
                await HandleLarkCardCreateCompletionAsync(evt);
                return;
            case LarkCardOperationPhase.Stream:
                await HandleLarkCardStreamCompletionAsync(evt);
                return;
            case LarkCardOperationPhase.Finalize:
                await HandleLarkCardFinalizeCompletionAsync(evt);
                return;
            default:
                _logger.LogDebug(
                    "Ignoring Lark card operation signal with unspecified operation. operationId={OperationId}",
                    evt.OperationId);
                return;
        }
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleLarkCardOperationTimeoutFiredAsync(LarkCardOperationTimeoutFiredEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return;

        var state = GetOrInitLarkCardDeliveryState();
        if (!MatchesLarkCardInFlight(state, evt.Operation, evt.Sequence, evt.OperationGeneration, evt.CardId))
            return;

        switch (evt.Operation)
        {
            case LarkCardOperationPhase.Create:
                await TransitionLarkCardDeliveryPhaseAsync(
                    correlationId,
                    state,
                    AgentRunLarkCardDeliveryPhase.CreationFailed,
                    terminalReason: "create_timeout",
                    fieldUpdate: s => s with { InFlight = null });
                return;
            case LarkCardOperationPhase.Stream:
            {
                var recovered = await TransitionLarkCardDeliveryPhaseAsync(
                    correlationId,
                    state,
                    AgentRunLarkCardDeliveryPhase.Streaming,
                    terminalReason: "stream_timeout",
                    fieldUpdate: s => s with
                    {
                        InFlight = null,
                        PendingAccumulatedText = null,
                    });
                await ContinueLarkCardCoalescedWorkAsync(correlationId, recovered, sourceChunk: null);
                return;
            }
            case LarkCardOperationPhase.Finalize:
                await TransitionLarkCardDeliveryPhaseAsync(
                    correlationId,
                    state,
                    AgentRunLarkCardDeliveryPhase.Terminated,
                    terminalReason: "finalize_timeout",
                    fieldUpdate: s => s with { InFlight = null });
                await CompleteCardStreamedDeliveryAsync(
                    correlationId,
                    NormalizeOptional(evt.CommandId) ?? state.PendingFinalizeCommandId ?? BuildLlmReplyCommandId(correlationId),
                    evt.Activity,
                    state.CardMessageId ?? evt.CardMessageId ?? string.Empty,
                    state.LastFlushedText,
                    deliveryFailure: new LlmReplyDeliveryFailedEvent
                    {
                        CorrelationId = correlationId,
                        RunId = State.RunId ?? string.Empty,
                        FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                        ErrorCode = "finalize_timeout",
                        ErrorMessage = "Card finalize operation timed out.",
                    },
                    appendedHistory: []);
                return;
        }
    }

    private async Task<bool> TryCompleteCardStreamedReplyAsync(
        NeedsLlmReplyEvent request,
        string runId,
        string replyText,
        MessageContent? outboundIntent,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory)
    {
        var correlationId = NormalizeOptional(request.CorrelationId);
        if (correlationId is null)
            return false;

        var state = GetOrInitLarkCardDeliveryState();
        if (state.Phase is AgentRunLarkCardDeliveryPhase.Idle
            or AgentRunLarkCardDeliveryPhase.CreationFailed)
        {
            if (HasPendingCardDeliveryCompletion() &&
                state.Phase is AgentRunLarkCardDeliveryPhase.CreationFailed &&
                IsTerminalLarkCardTextFallbackPhase(State.LarkCardTextFallback?.Phase))
            {
                await DispatchPendingCardDeliveryCompletionAsync();
                await TryFinalizeAfterDispatchAsync(BuildCardDeliveryCompletionRetryRequest(), runId);
                return true;
            }

            if (state.Phase is AgentRunLarkCardDeliveryPhase.CreationFailed)
            {
                var fallbackFinalText = outboundIntent?.Text ?? replyText;
                await CompleteLarkCardTextFallbackAsync(
                    request.Activity?.Clone() ?? new ChatActivity(),
                    correlationId,
                    fallbackFinalText,
                    appendedHistory);
                return true;
            }

            return false;
        }

        if (state.Phase is AgentRunLarkCardDeliveryPhase.Completed
                       or AgentRunLarkCardDeliveryPhase.Aborted
                       or AgentRunLarkCardDeliveryPhase.Terminated)
        {
            if (HasPendingCardDeliveryCompletion())
            {
                await DispatchPendingCardDeliveryCompletionAsync();
                await TryFinalizeAfterDispatchAsync(BuildCardDeliveryCompletionRetryRequest(), runId);
            }
            return true;
        }

        var finalText = outboundIntent?.Text ?? replyText;
        var commandId = BuildLlmReplyCommandId(correlationId);
        if (state.InFlight is not null)
        {
            await PersistLarkCardCoalescedStateAsync(
                correlationId,
                state,
                finalizeText: finalText,
                finalizeCommandId: commandId,
                appendedHistory: appendedHistory);
            return true;
        }

        if (ShouldSkipLarkCardStreamingForUnavailable(state, LarkCardDeliveryGuardSource.Finalize))
            return false;

        var finalDiffers = !string.IsNullOrWhiteSpace(finalText)
            && !string.Equals(finalText, state.LastFlushedText, StringComparison.Ordinal);
        var nextSequence = state.Sequence + 1;
        var activityForToken = request.Activity?.Clone() ?? new ChatActivity();
        RestoreRuntimeTransportCredentials(
            activityForToken,
            BuildNyxRelayRuntimeContext(
                correlationId,
                activityForToken,
                request.ReplyToken,
                request.ReplyTokenExpiresAtUnixMs));

        var generation = NextLarkCardOperationGeneration(state);
        await TransitionLarkCardDeliveryPhaseAsync(
            correlationId,
            state,
            AgentRunLarkCardDeliveryPhase.Streaming,
            fieldUpdate: s => s with
            {
                InFlight = new LarkCardOperationInFlight(LarkCardOperationPhase.Finalize, nextSequence, generation),
                OperationGeneration = generation,
                PendingFinalizeText = finalText,
                PendingFinalizeCommandId = commandId,
                PendingAppendedHistory = appendedHistory.Select(entry => entry.Clone()).ToArray(),
            });
        await ScheduleLarkCardOperationTimeoutAsync(
            correlationId,
            LarkCardOperationPhase.Finalize,
            nextSequence,
            generation,
            state.CardId,
            state.CardMessageId,
            commandId,
            activityForToken,
            finalText,
            state.LastFlushedText,
            CancellationToken.None);
        await StartLarkCardFinalizeOperationAsync(
            activityForToken,
            correlationId,
            commandId,
            state,
            finalText,
            finalDiffers,
            appendedHistory,
            nextSequence,
            generation);
        return true;
    }

    private async Task<bool> HandleLarkCardStreamingChunkCoreAsync(
        LlmReplyCardStreamChunkEvent evt,
        string correlationId)
    {
        var state = GetOrInitLarkCardDeliveryState();
        if (state.Phase is AgentRunLarkCardDeliveryPhase.CreationFailed)
            return false;

        if (ShouldSkipLarkCardStreamingForUnavailable(state, LarkCardDeliveryGuardSource.AcceptInterimChunk))
            return true;

        if (state.Phase is AgentRunLarkCardDeliveryPhase.Idle)
        {
            var generation = NextLarkCardOperationGeneration(state);
            await TransitionLarkCardDeliveryPhaseAsync(
                correlationId,
                state,
                AgentRunLarkCardDeliveryPhase.Creating,
                fieldUpdate: s => s with
                {
                    InFlight = new LarkCardOperationInFlight(LarkCardOperationPhase.Create, 1, generation),
                    OperationGeneration = generation,
                    PendingAccumulatedText = evt.AccumulatedText,
                });
            await ScheduleLarkCardOperationTimeoutAsync(
                correlationId,
                LarkCardOperationPhase.Create,
                1,
                generation,
                cardId: null,
                cardMessageId: null,
                commandId: BuildLlmReplyCommandId(correlationId),
                activity: null,
                finalText: null,
                lastFlushedText: null,
                CancellationToken.None);
            await StartLarkCardCreateOperationAsync(evt, correlationId, state.StreamingElementId, 1, generation);
            return true;
        }

        if (state.InFlight is not null)
        {
            await PersistLarkCardCoalescedStateAsync(correlationId, state, evt.AccumulatedText);
            return true;
        }

        var nextSequence = state.Sequence + 1;
        var streamGeneration = NextLarkCardOperationGeneration(state);
        await TransitionLarkCardDeliveryPhaseAsync(
            correlationId,
            state,
            AgentRunLarkCardDeliveryPhase.Streaming,
            fieldUpdate: s => s with
            {
                InFlight = new LarkCardOperationInFlight(LarkCardOperationPhase.Stream, nextSequence, streamGeneration),
                OperationGeneration = streamGeneration,
                PendingAccumulatedText = evt.AccumulatedText,
            });
        await ScheduleLarkCardOperationTimeoutAsync(
            correlationId,
            LarkCardOperationPhase.Stream,
            nextSequence,
            streamGeneration,
            state.CardId,
            state.CardMessageId,
            BuildLlmReplyCommandId(correlationId),
            activity: null,
            finalText: null,
            lastFlushedText: state.LastFlushedText,
            CancellationToken.None);
        await StartLarkCardStreamOperationAsync(evt, correlationId, state, nextSequence, streamGeneration);
        return true;
    }

    private async Task HandleLarkCardCreateCompletionAsync(LarkCardOperationCompletedEvent evt)
    {
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return;

        var state = GetOrInitLarkCardDeliveryState();
        if (!MatchesLarkCardInFlight(state, LarkCardOperationPhase.Create, evt.Sequence, evt.OperationGeneration))
            return;

        var result = ToCreateResult(evt);
        if (!result.Success)
        {
            if (result.IsPostSendFailure)
            {
                _logger.LogWarning(
                    "Card post-send failure; terminating turn without text-edit fallback. runId={RunId} correlation={CorrelationId} code={ErrorCode} cardId={CardId}",
                    State.RunId,
                    evt.CorrelationId,
                    result.ErrorCode,
                    result.CardId);
                var terminated = await TransitionLarkCardDeliveryPhaseAsync(
                    correlationId,
                    state,
                    AgentRunLarkCardDeliveryPhase.Terminated,
                    terminalReason: $"create_post_send_failed:{result.ErrorCode}",
                    fieldUpdate: s => s with
                    {
                        CardId = NormalizeOptional(result.CardId),
                        CardMessageId = NormalizeOptional(result.CardMessageId),
                        OriginalCardId = NormalizeOptional(result.CardId),
                        InFlight = null,
                    });
                await CompleteCardStreamedDeliveryAsync(
                    correlationId,
                    BuildLlmReplyCommandId(correlationId),
                    evt.Chunk?.Activity,
                    terminated.CardMessageId ?? string.Empty,
                    terminated.LastFlushedText,
                    deliveryFailure: null,
                    appendedHistory: []);
                return;
            }

            _logger.LogInformation(
                "Card create failed; falling back to text-edit for the rest of this turn. runId={RunId} correlation={CorrelationId} code={ErrorCode} summary={ErrorSummary}",
                State.RunId,
                evt.CorrelationId,
                result.ErrorCode,
                TrimLogValue(result.ErrorSummary, 512));
            await TransitionLarkCardDeliveryPhaseAsync(
                correlationId,
                state,
                AgentRunLarkCardDeliveryPhase.CreationFailed,
                terminalReason: $"create_failed:{result.ErrorCode}",
                fieldUpdate: s => s with { InFlight = null });
            if (evt.Chunk is not null)
                await StartLarkCardTextFallbackAsync(evt.Chunk, result.ErrorCode, result.ErrorSummary);
            return;
        }

        var accumulatedText = state.PendingAccumulatedText ?? evt.Chunk?.AccumulatedText ?? string.Empty;
        var streaming = await TransitionLarkCardDeliveryPhaseAsync(
            correlationId,
            state,
            AgentRunLarkCardDeliveryPhase.Streaming,
            fieldUpdate: s => s with
            {
                CardId = NormalizeOptional(result.CardId),
                CardMessageId = NormalizeOptional(result.CardMessageId),
                OriginalCardId = NormalizeOptional(result.CardId),
                LastFlushedText = accumulatedText,
                Sequence = evt.Sequence,
                InFlight = null,
                PendingAccumulatedText = null,
            });
        await ContinueLarkCardCoalescedWorkAsync(correlationId, streaming, evt.Chunk);
    }

    private async Task HandleLarkCardStreamCompletionAsync(LarkCardOperationCompletedEvent evt)
    {
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return;

        var state = GetOrInitLarkCardDeliveryState();
        if (!MatchesLarkCardInFlight(
                state,
                LarkCardOperationPhase.Stream,
                evt.Sequence,
                evt.OperationGeneration,
                evt.CardId))
        {
            return;
        }

        var result = ToStreamResult(evt);
        if (!result.Success)
        {
            if (result.IsRateLimited)
            {
                _logger.LogDebug(
                    "Card stream rate-limited; dropping frame. runId={RunId} correlation={CorrelationId} seq={Sequence}",
                    State.RunId,
                    evt.CorrelationId,
                    evt.Sequence);
                var recovered = await TransitionLarkCardDeliveryPhaseAsync(
                    correlationId,
                    state,
                    AgentRunLarkCardDeliveryPhase.Streaming,
                    fieldUpdate: s => s with
                    {
                        InFlight = null,
                        PendingAccumulatedText = null,
                    });
                await ContinueLarkCardCoalescedWorkAsync(correlationId, recovered, sourceChunk: null);
                return;
            }

            if (result.IsTableLimitExceeded || result.IsCardUnavailable)
            {
                _logger.LogWarning(
                    "Card stream terminal failure; ending turn. runId={RunId} correlation={CorrelationId} code={ErrorCode}",
                    State.RunId,
                    evt.CorrelationId,
                    result.ErrorCode);
                var terminated = await TransitionLarkCardDeliveryPhaseAsync(
                    correlationId,
                    state,
                    AgentRunLarkCardDeliveryPhase.Terminated,
                    terminalReason: $"stream_failed:{result.ErrorCode}",
                    fieldUpdate: s => s with { InFlight = null });
                await CompleteCardStreamedDeliveryAsync(
                    correlationId,
                    BuildLlmReplyCommandId(correlationId),
                    evt.Chunk?.Activity,
                    terminated.CardMessageId ?? string.Empty,
                    terminated.LastFlushedText,
                    deliveryFailure: null,
                    appendedHistory: []);
                return;
            }

            var continued = await TransitionLarkCardDeliveryPhaseAsync(
                correlationId,
                state,
                AgentRunLarkCardDeliveryPhase.Streaming,
                fieldUpdate: s => s with
                {
                    InFlight = null,
                    PendingAccumulatedText = null,
                });
            await ContinueLarkCardCoalescedWorkAsync(correlationId, continued, evt.Chunk);
            return;
        }

        var ackedText = evt.Chunk?.AccumulatedText ?? state.LastFlushedText;
        var pendingText = string.Equals(state.PendingAccumulatedText, ackedText, StringComparison.Ordinal)
            ? null
            : state.PendingAccumulatedText;
        var updated = await TransitionLarkCardDeliveryPhaseAsync(
            correlationId,
            state,
            AgentRunLarkCardDeliveryPhase.Streaming,
            fieldUpdate: s => s with
            {
                LastFlushedText = ackedText,
                Sequence = evt.Sequence,
                InFlight = null,
                PendingAccumulatedText = pendingText,
            });
        await ContinueLarkCardCoalescedWorkAsync(correlationId, updated, evt.Chunk);
    }

    private async Task HandleLarkCardFinalizeCompletionAsync(LarkCardOperationCompletedEvent evt)
    {
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return;

        var state = GetOrInitLarkCardDeliveryState();
        if (!MatchesLarkCardInFlight(
                state,
                LarkCardOperationPhase.Finalize,
                evt.Sequence,
                evt.OperationGeneration,
                evt.CardId))
        {
            return;
        }

        var result = ToFinalizeResult(evt);
        var finalText = state.PendingFinalizeText ?? evt.FinalText ?? string.Empty;
        var commandId = state.PendingFinalizeCommandId ?? evt.CommandId ?? BuildLlmReplyCommandId(correlationId);
        var visibleText = result.FinalTextWritten ? finalText : state.LastFlushedText;
        var cardMessageId = state.CardMessageId ?? evt.CardMessageId ?? string.Empty;
        if (result.Success)
        {
            await TransitionLarkCardDeliveryPhaseAsync(
                correlationId,
                state,
                AgentRunLarkCardDeliveryPhase.Completed,
                terminalReason: "completed",
                fieldUpdate: s => s with { InFlight = null });
            await CompleteCardStreamedDeliveryAsync(
                correlationId,
                commandId,
                evt.Activity,
                cardMessageId,
                visibleText,
                deliveryFailure: null,
                evt.AppendedHistory.ToArray());
            return;
        }

        _logger.LogWarning(
            "Card finalize failed; persisting partial. runId={RunId} correlation={CorrelationId} code={ErrorCode}",
            State.RunId,
            evt.CorrelationId,
            result.ErrorCode);
        await TransitionLarkCardDeliveryPhaseAsync(
            correlationId,
            state,
            AgentRunLarkCardDeliveryPhase.Terminated,
            terminalReason: $"finalize_failed:{result.ErrorCode}",
            fieldUpdate: s => s with { InFlight = null });
        await CompleteCardStreamedDeliveryAsync(
            correlationId,
            commandId,
            evt.Activity,
            cardMessageId,
            visibleText,
            new LlmReplyDeliveryFailedEvent
            {
                CorrelationId = correlationId,
                RunId = State.RunId ?? string.Empty,
                FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                ErrorCode = result.ErrorCode ?? string.Empty,
                ErrorMessage = result.ErrorSummary ?? string.Empty,
            },
            evt.AppendedHistory.ToArray());
    }

    private async Task ContinueLarkCardCoalescedWorkAsync(
        string correlationId,
        LarkCardDeliveryRuntimeState state,
        LlmReplyCardStreamChunkEvent? sourceChunk)
    {
        if (state.Phase is not AgentRunLarkCardDeliveryPhase.Streaming || state.InFlight is not null)
            return;

        if (state.PendingFinalizeText is not null)
        {
            var finalText = state.PendingFinalizeText;
            var commandId = state.PendingFinalizeCommandId ?? BuildLlmReplyCommandId(correlationId);
            var activity = sourceChunk?.Activity?.Clone() ?? new ChatActivity();
            var runtimeContext = BuildNyxRelayRuntimeContext(
                correlationId,
                activity,
                sourceChunk?.ReplyToken,
                sourceChunk?.ReplyTokenExpiresAtUnixMs ?? 0);
            RestoreRuntimeTransportCredentials(activity, runtimeContext);
            var nextSequence = state.Sequence + 1;
            var generation = NextLarkCardOperationGeneration(state);
            var finalDiffers = !string.IsNullOrWhiteSpace(finalText)
                && !string.Equals(finalText, state.LastFlushedText, StringComparison.Ordinal);
            await TransitionLarkCardDeliveryPhaseAsync(
                correlationId,
                state,
                AgentRunLarkCardDeliveryPhase.Streaming,
                fieldUpdate: s => s with
                {
                    InFlight = new LarkCardOperationInFlight(LarkCardOperationPhase.Finalize, nextSequence, generation),
                    OperationGeneration = generation,
                });
            await ScheduleLarkCardOperationTimeoutAsync(
                correlationId,
                LarkCardOperationPhase.Finalize,
                nextSequence,
                generation,
                state.CardId,
                state.CardMessageId,
                commandId,
                activity,
                finalText,
                state.LastFlushedText,
                CancellationToken.None);
            await StartLarkCardFinalizeOperationAsync(
                activity,
                correlationId,
                commandId,
                state,
                finalText,
                finalDiffers,
                state.PendingAppendedHistory,
                nextSequence,
                generation);
            return;
        }

        if (state.PendingAccumulatedText is null || sourceChunk is null)
            return;

        var nextChunk = sourceChunk.Clone();
        nextChunk.AccumulatedText = state.PendingAccumulatedText;
        var streamSequence = state.Sequence + 1;
        var streamGeneration = NextLarkCardOperationGeneration(state);
        await TransitionLarkCardDeliveryPhaseAsync(
            correlationId,
            state,
            AgentRunLarkCardDeliveryPhase.Streaming,
            fieldUpdate: s => s with
            {
                InFlight = new LarkCardOperationInFlight(LarkCardOperationPhase.Stream, streamSequence, streamGeneration),
                OperationGeneration = streamGeneration,
            });
        await ScheduleLarkCardOperationTimeoutAsync(
            correlationId,
            LarkCardOperationPhase.Stream,
            streamSequence,
            streamGeneration,
            state.CardId,
            state.CardMessageId,
            BuildLlmReplyCommandId(correlationId),
            activity: null,
            finalText: null,
            lastFlushedText: state.LastFlushedText,
            CancellationToken.None);
        await StartLarkCardStreamOperationAsync(nextChunk, correlationId, state, streamSequence, streamGeneration);
    }

    private async Task CompleteCardStreamedDeliveryAsync(
        string correlationId,
        string commandId,
        ChatActivity? eventActivity,
        string cardMessageId,
        string outboundText,
        LlmReplyDeliveryFailedEvent? deliveryFailure,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory)
    {
        var runId = NormalizeOptional(State.RunId) ?? string.Empty;
        var request = new NeedsLlmReplyEvent
        {
            RunId = runId,
            CorrelationId = correlationId,
            TargetActorId = State.TargetActorId ?? string.Empty,
            Activity = eventActivity?.Clone() ?? new ChatActivity(),
        };

        var normalizedOutboundText = outboundText ?? string.Empty;
        var completedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var completion = new AgentRunLarkCardDeliveryCompletion
        {
            CorrelationId = correlationId,
            RunId = runId,
            TargetActorId = request.TargetActorId,
            CommandId = commandId,
            Activity = CloneForDurableState(eventActivity) ?? new ChatActivity(),
            CardMessageId = cardMessageId ?? string.Empty,
            OutboundText = normalizedOutboundText,
            CompletedAtUnixMs = completedAtUnixMs,
            Outcome = deliveryFailure is null
                ? AgentRunLarkCardDeliveryCompletionOutcome.Completed
                : AgentRunLarkCardDeliveryCompletionOutcome.Failed,
        };
        completion.AppendedHistory.AddRange(appendedHistory.Select(entry => entry.Clone()));
        if (deliveryFailure is not null)
            completion.DeliveryFailure = deliveryFailure.Clone();

        await PersistReplyProducedWithCardCompletionAsync(
            request,
            runId,
            normalizedOutboundText,
            new MessageContent { Text = normalizedOutboundText },
            LlmReplyTerminalState.Completed,
            deliveryFailure is null ? string.Empty : deliveryFailure.ErrorCode,
            deliveryFailure is null ? string.Empty : deliveryFailure.ErrorMessage,
            appendedHistory,
            completion);

        try
        {
            await DispatchPendingCardDeliveryCompletionAsync();
        }
        catch (AgentRunOutputDispatchException ex)
        {
            if (await TryHandleOutputDispatchFailureAsync(request, runId, ex))
                return;

            throw;
        }
        await TryFinalizeAfterDispatchAsync(request, runId);
    }

    private bool HasPendingCardDeliveryCompletion() =>
        State.PendingCardDeliveryCompletion is
        {
            TargetActorId.Length: > 0,
            CorrelationId.Length: > 0,
        };

    private NeedsLlmReplyEvent BuildCardDeliveryCompletionRetryRequest()
    {
        var completion = State.PendingCardDeliveryCompletion;
        return new NeedsLlmReplyEvent
        {
            RunId = NormalizeOptional(completion?.RunId) ?? State.RunId ?? string.Empty,
            CorrelationId = NormalizeOptional(completion?.CorrelationId) ?? State.CorrelationId ?? string.Empty,
            TargetActorId = NormalizeOptional(completion?.TargetActorId) ?? State.TargetActorId ?? string.Empty,
            Activity = completion?.Activity?.Clone() ?? new ChatActivity(),
        };
    }

    private async Task DispatchPendingCardDeliveryCompletionAsync()
    {
        var completion = State.PendingCardDeliveryCompletion;
        var targetActorId = NormalizeOptional(completion?.TargetActorId) ?? NormalizeOptional(State.TargetActorId);
        if (targetActorId is null)
            return;

        var completed = ToLarkCardDeliveryCompletedEvent(completion);
        try
        {
            await SendToAsync(targetActorId, completed, CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new AgentRunOutputDispatchException(
                $"Failed to send Lark card delivery completion to conversation actor '{targetActorId}'.",
                ex);
        }
    }

    private static LarkCardDeliveryCompletedEvent ToLarkCardDeliveryCompletedEvent(
        AgentRunLarkCardDeliveryCompletion? completion)
    {
        var completed = new LarkCardDeliveryCompletedEvent
        {
            CorrelationId = completion?.CorrelationId ?? string.Empty,
            RunId = completion?.RunId ?? string.Empty,
            CommandId = completion?.CommandId ?? string.Empty,
            Activity = completion?.Activity?.Clone() ?? new ChatActivity(),
            CardMessageId = completion?.CardMessageId ?? string.Empty,
            OutboundText = completion?.OutboundText ?? string.Empty,
            CompletedAtUnixMs = completion?.CompletedAtUnixMs ?? 0,
        };
        completed.AppendedHistory.AddRange(
            completion?.AppendedHistory.Select(entry => entry.Clone()) ?? []);
        if (completion?.DeliveryFailure is not null)
            completed.DeliveryFailure = completion.DeliveryFailure.Clone();
        return completed;
    }

    private Task StartLarkCardTextFallbackAsync(
        LlmReplyCardStreamChunkEvent chunk,
        string? errorCode,
        string? errorSummary) =>
        StartLarkCardTextFallbackAsync(
            chunk.Activity?.Clone() ?? new ChatActivity(),
            NormalizeOptional(chunk.CorrelationId) ?? State.CorrelationId ?? string.Empty,
            errorCode,
            errorSummary);

    private async Task StartLarkCardTextFallbackAsync(
        ChatActivity activity,
        string correlationId,
        string? errorCode,
        string? errorSummary)
    {
        if (NormalizeLarkCardTextFallbackPhase(State.LarkCardTextFallback?.Phase)
            is AgentRunLarkCardTextFallbackPhase.StatusSent
            or AgentRunLarkCardTextFallbackPhase.Completed
            or AgentRunLarkCardTextFallbackPhase.Failed)
        {
            return;
        }

        if (!TryBuildLarkTextFallbackContext(
                activity,
                out var nyxUserAccessToken,
                out var providerSlug,
                out var primaryTarget,
                out var fallbackTarget,
                out var failureCode,
                out var failureMessage))
        {
            await PersistLarkCardTextFallbackChangedAsync(
                correlationId,
                AgentRunLarkCardTextFallbackPhase.Failed,
                lastErrorCode: failureCode,
                lastErrorMessage: failureMessage);
            return;
        }

        try
        {
            var dispatcher = ResolveLarkOutboundDispatcher();
            if (dispatcher is null)
            {
                await PersistLarkCardTextFallbackChangedAsync(
                    correlationId,
                    AgentRunLarkCardTextFallbackPhase.Failed,
                    lastErrorCode: "lark_text_fallback_delivery_unavailable",
                    lastErrorMessage: "ILarkOutboundDispatcher is not registered for Lark text fallback delivery.");
                return;
            }

            var result = await ExecuteLarkCardTextFallbackOperationAsync(
                ct => dispatcher.SendNewMessageAsync(
                    new LarkSendNewMessageRequest(
                        nyxUserAccessToken,
                        providerSlug,
                        LarkMessageTypes.Text,
                        LarkCardTextFallbackPolicy.BuildTextContentJson(LarkCardTextFallbackPolicy.StatusText),
                        primaryTarget,
                        fallbackTarget),
                    ct),
                "status_send_timeout");

            if (result.TimedOut)
            {
                const string timeoutMessage = "Lark text fallback status send timed out.";
                await PersistLarkCardTextFallbackChangedAsync(
                    correlationId,
                    AgentRunLarkCardTextFallbackPhase.Failed,
                    lastErrorCode: "status_send_timeout",
                    lastErrorMessage: timeoutMessage);
                return;
            }

            if (!result.Value.Succeeded || string.IsNullOrWhiteSpace(result.Value.MessageId))
            {
                await PersistLarkCardTextFallbackChangedAsync(
                    correlationId,
                    AgentRunLarkCardTextFallbackPhase.Failed,
                    lastErrorCode: BuildLarkFallbackFailureCode("status_send_failed", result.Value.LarkCode),
                    lastErrorMessage: result.Value.Detail);
                return;
            }

            await PersistLarkCardTextFallbackChangedAsync(
                correlationId,
                AgentRunLarkCardTextFallbackPhase.StatusSent,
                statusMessageId: result.Value.MessageId,
                lastDeliveredText: LarkCardTextFallbackPolicy.StatusText,
                segmentsDelivered: 1,
                totalSegments: 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to send Lark card text fallback status message. runId={RunId} correlation={CorrelationId} cardError={CardError} summary={ErrorSummary}",
                State.RunId,
                correlationId,
                errorCode,
                TrimLogValue(errorSummary, 256));
            await PersistLarkCardTextFallbackChangedAsync(
                correlationId,
                AgentRunLarkCardTextFallbackPhase.Failed,
                lastErrorCode: "status_send_exception",
                lastErrorMessage: ex.Message);
        }
    }

    private Task<LarkTextFallbackTimedOperationResult<LarkSendNewMessageResult>> SendLarkCardTextFallbackMessageAsync(
        ILarkOutboundDispatcher dispatcher,
        string nyxUserAccessToken,
        string providerSlug,
        LarkReceiveTarget primaryTarget,
        LarkReceiveTarget? fallbackTarget,
        string text,
        string timeoutCode) =>
        ExecuteLarkCardTextFallbackOperationAsync(
            ct => dispatcher.SendNewMessageAsync(
                new LarkSendNewMessageRequest(
                    nyxUserAccessToken,
                    providerSlug,
                    LarkMessageTypes.Text,
                    LarkCardTextFallbackPolicy.BuildTextContentJson(text),
                    primaryTarget,
                    fallbackTarget),
                    ct),
            timeoutCode);

    private Task CompleteLarkCardTextFallbackAsync(
        LlmReplyCardStreamChunkEvent chunk,
        string finalText,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory) =>
        CompleteLarkCardTextFallbackAsync(
            chunk.Activity?.Clone() ?? new ChatActivity(),
            NormalizeOptional(chunk.CorrelationId) ?? State.CorrelationId ?? string.Empty,
            finalText,
            appendedHistory);

    private Task DeliverLarkCardTextFallbackOnlyAsync(
        LlmReplyCardStreamChunkEvent chunk,
        string finalText) =>
        DeliverLarkCardTextFallbackOnlyAsync(
            chunk.Activity?.Clone() ?? new ChatActivity(),
            NormalizeOptional(chunk.CorrelationId) ?? State.CorrelationId ?? string.Empty,
            finalText);

    private async Task DeliverLarkCardTextFallbackOnlyAsync(
        ChatActivity activity,
        string correlationId,
        string finalText)
    {
        await DeliverLarkCardTextFallbackFinalAndPersistAsync(activity, correlationId, finalText);
    }

    private async Task CompleteLarkCardTextFallbackAsync(
        ChatActivity activity,
        string correlationId,
        string finalText,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory)
    {
        var result = await DeliverLarkCardTextFallbackFinalAndPersistAsync(activity, correlationId, finalText);
        if (result.AlreadyCompleted && result.Succeeded)
        {
            await CompleteCardStreamedDeliveryAsync(
                correlationId,
                BuildLlmReplyCommandId(correlationId),
                activity,
                result.StatusMessageId,
                finalText ?? string.Empty,
                deliveryFailure: null,
                appendedHistory);
            return;
        }

        await CompleteCardStreamedDeliveryAsync(
            correlationId,
            BuildLlmReplyCommandId(correlationId),
            activity,
            result.StatusMessageId,
            result.VisibleText,
            result.Succeeded
                ? null
                : BuildLarkTextFallbackFailure(correlationId, result.ErrorCode, result.ErrorMessage),
            appendedHistory);
    }

    private async Task<LarkTextFallbackPersistedFinalDelivery> DeliverLarkCardTextFallbackFinalAndPersistAsync(
        ChatActivity activity,
        string correlationId,
        string finalText)
    {
        if (NormalizeLarkCardTextFallbackPhase(State.LarkCardTextFallback?.Phase)
            is not AgentRunLarkCardTextFallbackPhase.StatusSent)
        {
            await StartLarkCardTextFallbackAsync(activity, correlationId, errorCode: null, errorSummary: null);
        }

        var fallback = State.LarkCardTextFallback;
        if (NormalizeLarkCardTextFallbackPhase(fallback?.Phase) is AgentRunLarkCardTextFallbackPhase.Failed)
        {
            return LarkTextFallbackPersistedFinalDelivery.Failed(
                fallback?.StatusMessageId ?? string.Empty,
                fallback?.LastDeliveredText ?? string.Empty,
                fallback?.LastErrorCode ?? string.Empty,
                fallback?.LastErrorMessage ?? string.Empty);
        }

        if (NormalizeLarkCardTextFallbackPhase(fallback?.Phase) is AgentRunLarkCardTextFallbackPhase.Completed)
        {
            var completedStatusMessageId = fallback?.StatusMessageId ?? string.Empty;
            var completedVisibleText = fallback?.LastDeliveredText ?? string.Empty;
            var authoritativeSegments = LarkCardTextFallbackPolicy.SegmentText(finalText ?? string.Empty);
            var authoritativeVisibleText = string.Join("\n", authoritativeSegments);
            if (string.Equals(completedVisibleText, authoritativeVisibleText, StringComparison.Ordinal))
            {
                return LarkTextFallbackPersistedFinalDelivery.Completed(
                    completedStatusMessageId,
                    completedVisibleText,
                    alreadyCompleted: true);
            }

            return await UpdateCompletedLarkCardTextFallbackFinalAndPersistAsync(
                activity,
                correlationId,
                completedStatusMessageId,
                authoritativeSegments,
                authoritativeVisibleText);
        }

        if (!TryBuildLarkTextFallbackContext(
                activity,
                out var nyxUserAccessToken,
                out var providerSlug,
                out var primaryTarget,
                out var fallbackTarget,
                out var failureCode,
                out var failureMessage))
        {
            await PersistLarkCardTextFallbackChangedAsync(
                correlationId,
                AgentRunLarkCardTextFallbackPhase.Failed,
                lastErrorCode: failureCode,
                lastErrorMessage: failureMessage);
            return LarkTextFallbackPersistedFinalDelivery.Failed(
                fallback?.StatusMessageId ?? string.Empty,
                fallback?.LastDeliveredText ?? string.Empty,
                failureCode,
                failureMessage);
        }

        fallback = State.LarkCardTextFallback;
        var statusMessageId = NormalizeOptional(fallback?.StatusMessageId);
        if (statusMessageId is null)
        {
            const string statusMessageFailureCode = "status_message_unavailable";
            const string statusMessageFailureMessage = "Lark text fallback status message id is unavailable.";
            await PersistLarkCardTextFallbackChangedAsync(
                correlationId,
                AgentRunLarkCardTextFallbackPhase.Failed,
                lastErrorCode: statusMessageFailureCode,
                lastErrorMessage: statusMessageFailureMessage);
            return LarkTextFallbackPersistedFinalDelivery.Failed(
                string.Empty,
                string.Empty,
                statusMessageFailureCode,
                statusMessageFailureMessage);
        }

        var segments = LarkCardTextFallbackPolicy.SegmentText(finalText ?? string.Empty);
        var finalDelivery = await DeliverLarkCardTextFallbackFinalAsync(
            correlationId,
            nyxUserAccessToken,
            providerSlug,
            primaryTarget,
            fallbackTarget,
            statusMessageId,
            segments);

        var visibleText = string.Join("\n", segments);
        if (finalDelivery.Succeeded)
        {
            await PersistLarkCardTextFallbackChangedAsync(
                correlationId,
                AgentRunLarkCardTextFallbackPhase.Completed,
                lastDeliveredText: visibleText,
                segmentsDelivered: finalDelivery.SegmentsDelivered,
                totalSegments: segments.Count,
                lastErrorCode: string.Empty,
                lastErrorMessage: string.Empty);
            return LarkTextFallbackPersistedFinalDelivery.Completed(
                statusMessageId,
                visibleText,
                alreadyCompleted: false);
        }

        await PersistLarkCardTextFallbackChangedAsync(
            correlationId,
            AgentRunLarkCardTextFallbackPhase.Failed,
            lastDeliveredText: finalDelivery.LastDeliveredText,
            segmentsDelivered: finalDelivery.SegmentsDelivered,
            totalSegments: segments.Count,
            lastErrorCode: finalDelivery.ErrorCode,
            lastErrorMessage: finalDelivery.ErrorMessage);
        return LarkTextFallbackPersistedFinalDelivery.Failed(
            statusMessageId,
            finalDelivery.LastDeliveredText,
            finalDelivery.ErrorCode,
            finalDelivery.ErrorMessage);
    }

    private async Task<LarkTextFallbackPersistedFinalDelivery> UpdateCompletedLarkCardTextFallbackFinalAndPersistAsync(
        ChatActivity activity,
        string correlationId,
        string statusMessageId,
        IReadOnlyList<string> authoritativeSegments,
        string authoritativeVisibleText)
    {
        if (!TryBuildLarkTextFallbackContext(
                activity,
                out var nyxUserAccessToken,
                out var providerSlug,
                out var primaryTarget,
                out var fallbackTarget,
                out var failureCode,
                out var failureMessage))
        {
            await PersistLarkCardTextFallbackChangedAsync(
                correlationId,
                AgentRunLarkCardTextFallbackPhase.Failed,
                lastErrorCode: failureCode,
                lastErrorMessage: failureMessage);
            return LarkTextFallbackPersistedFinalDelivery.Failed(
                statusMessageId,
                State.LarkCardTextFallback?.LastDeliveredText ?? string.Empty,
                failureCode,
                failureMessage);
        }

        if (string.IsNullOrWhiteSpace(statusMessageId))
        {
            const string statusMessageFailureCode = "status_message_unavailable";
            const string statusMessageFailureMessage = "Lark text fallback status message id is unavailable.";
            await PersistLarkCardTextFallbackChangedAsync(
                correlationId,
                AgentRunLarkCardTextFallbackPhase.Failed,
                lastErrorCode: statusMessageFailureCode,
                lastErrorMessage: statusMessageFailureMessage);
            return LarkTextFallbackPersistedFinalDelivery.Failed(
                string.Empty,
                State.LarkCardTextFallback?.LastDeliveredText ?? string.Empty,
                statusMessageFailureCode,
                statusMessageFailureMessage);
        }

        var finalDelivery = await DeliverLarkCardTextFallbackFinalAsync(
            correlationId,
            nyxUserAccessToken,
            providerSlug,
            primaryTarget,
            fallbackTarget,
            statusMessageId,
            authoritativeSegments);

        if (finalDelivery.Succeeded)
        {
            await PersistLarkCardTextFallbackChangedAsync(
                correlationId,
                AgentRunLarkCardTextFallbackPhase.Completed,
                lastDeliveredText: authoritativeVisibleText,
                segmentsDelivered: finalDelivery.SegmentsDelivered,
                totalSegments: authoritativeSegments.Count,
                lastErrorCode: string.Empty,
                lastErrorMessage: string.Empty);
            return LarkTextFallbackPersistedFinalDelivery.Completed(
                statusMessageId,
                authoritativeVisibleText,
                alreadyCompleted: false);
        }

        await PersistLarkCardTextFallbackChangedAsync(
            correlationId,
            AgentRunLarkCardTextFallbackPhase.Failed,
            lastDeliveredText: finalDelivery.LastDeliveredText,
            segmentsDelivered: finalDelivery.SegmentsDelivered,
            totalSegments: authoritativeSegments.Count,
            lastErrorCode: finalDelivery.ErrorCode,
            lastErrorMessage: finalDelivery.ErrorMessage);
        return LarkTextFallbackPersistedFinalDelivery.Failed(
            statusMessageId,
            finalDelivery.LastDeliveredText,
            finalDelivery.ErrorCode,
            finalDelivery.ErrorMessage);
    }

    private async Task<LarkTextFallbackFinalDelivery> DeliverLarkCardTextFallbackFinalAsync(
        string correlationId,
        string nyxUserAccessToken,
        string providerSlug,
        LarkReceiveTarget primaryTarget,
        LarkReceiveTarget? fallbackTarget,
        string statusMessageId,
        IReadOnlyList<string> segments)
    {
        var delivered = 0;
        var lastDeliveredText = State.LarkCardTextFallback?.LastDeliveredText ?? string.Empty;
        var editPort = ResolveLarkTextMessageEditPort();
        if (editPort is null)
        {
            return LarkTextFallbackFinalDelivery.Failed(
                delivered,
                lastDeliveredText,
                "lark_text_fallback_edit_unavailable",
                "ILarkTextMessageEditPort is not registered for Lark text fallback edit delivery.");
        }

        LarkTextMessageEditResult edit;
        try
        {
            var editResult = await ExecuteLarkCardTextFallbackOperationAsync(
                ct => editPort.EditAsync(
                    new LarkTextMessageEditRequest(
                        nyxUserAccessToken,
                        providerSlug,
                        statusMessageId,
                        segments[0]),
                    ct),
                "status_edit_timeout");
            if (editResult.TimedOut)
            {
                return LarkTextFallbackFinalDelivery.Failed(
                    delivered,
                    lastDeliveredText,
                    "status_edit_timeout",
                    "Lark text fallback status edit timed out.");
            }

            edit = editResult.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lark card text fallback final status edit threw. runId={RunId} correlation={CorrelationId}",
                State.RunId,
                correlationId);
            return LarkTextFallbackFinalDelivery.Failed(
                delivered,
                lastDeliveredText,
                "status_edit_exception",
                ex.Message);
        }

        if (edit.Succeeded)
        {
            delivered = 1;
            lastDeliveredText = segments[0];
        }
        else
        {
            _logger.LogWarning(
                "Lark card text fallback final status edit failed; sending final text as new messages. runId={RunId} correlation={CorrelationId} larkCode={LarkCode} detail={Detail}",
                State.RunId,
                correlationId,
                edit.LarkCode,
                edit.Detail);
            var freshResult = await SendLarkCardTextFallbackSegmentSafeAsync(
                nyxUserAccessToken,
                providerSlug,
                primaryTarget,
                fallbackTarget,
                segments[0],
                "final_send_exception_after_edit_failed",
                "final_send_timeout_after_edit_failed");
            if (!freshResult.Succeeded)
            {
                var failureCode = freshResult.ExceptionThrown
                    ? freshResult.ErrorCode
                    : BuildLarkFallbackFailureCode("final_send_failed_after_edit_failed", freshResult.LarkCode);
                return LarkTextFallbackFinalDelivery.Failed(
                    delivered,
                    lastDeliveredText,
                    failureCode,
                    AppendFailureDetail("status_edit_failed", edit.Detail, freshResult.Detail));
            }

            delivered = 1;
            lastDeliveredText = segments[0];
        }

        for (var i = 1; i < segments.Count; i++)
        {
            var sendResult = await SendLarkCardTextFallbackSegmentSafeAsync(
                nyxUserAccessToken,
                providerSlug,
                primaryTarget,
                fallbackTarget,
                segments[i],
                "segment_send_exception",
                "segment_send_timeout");
            if (!sendResult.Succeeded)
            {
                var failureCode = sendResult.ExceptionThrown
                    ? sendResult.ErrorCode
                    : BuildLarkFallbackFailureCode("segment_send_failed", sendResult.LarkCode);
                return LarkTextFallbackFinalDelivery.Failed(
                    delivered,
                    lastDeliveredText,
                    failureCode,
                    sendResult.Detail);
            }

            delivered++;
            lastDeliveredText = segments[i];
        }

        return LarkTextFallbackFinalDelivery.Completed(delivered, lastDeliveredText);
    }

    private async Task<LarkTextFallbackSegmentSendResult> SendLarkCardTextFallbackSegmentSafeAsync(
        string nyxUserAccessToken,
        string providerSlug,
        LarkReceiveTarget primaryTarget,
        LarkReceiveTarget? fallbackTarget,
        string text,
        string exceptionCode,
        string timeoutCode)
    {
        var dispatcher = ResolveLarkOutboundDispatcher();
        if (dispatcher is null)
        {
            return LarkTextFallbackSegmentSendResult.Failed(
                "lark_text_fallback_delivery_unavailable",
                null,
                "ILarkOutboundDispatcher is not registered for Lark text fallback delivery.",
                exceptionThrown: false);
        }

        try
        {
            var result = await SendLarkCardTextFallbackMessageAsync(
                dispatcher,
                nyxUserAccessToken,
                providerSlug,
                primaryTarget,
                fallbackTarget,
                text,
                timeoutCode);
            if (result.TimedOut)
            {
                return LarkTextFallbackSegmentSendResult.Failed(
                    timeoutCode,
                    null,
                    "Lark text fallback message send timed out.",
                    exceptionThrown: true);
            }

            return result.Value.Succeeded
                ? LarkTextFallbackSegmentSendResult.Sent(result.Value.MessageId ?? string.Empty)
                : LarkTextFallbackSegmentSendResult.Failed(
                    string.Empty,
                    result.Value.LarkCode,
                    result.Value.Detail,
                    exceptionThrown: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lark card text fallback segment send threw. runId={RunId} providerSlug={ProviderSlug}",
                State.RunId,
                providerSlug);
            return LarkTextFallbackSegmentSendResult.Failed(
                exceptionCode,
                null,
                ex.Message,
                exceptionThrown: true);
        }
    }

    private async Task PersistLarkCardTextFallbackChangedAsync(
        string correlationId,
        AgentRunLarkCardTextFallbackPhase phase,
        string? statusMessageId = null,
        string? lastDeliveredText = null,
        int? segmentsDelivered = null,
        int? totalSegments = null,
        string? lastErrorCode = null,
        string? lastErrorMessage = null)
    {
        var changed = new AgentRunLarkCardTextFallbackChangedEvent
        {
            RunId = State.RunId ?? string.Empty,
            CorrelationId = correlationId,
            ChangedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            Phase = phase,
        };
        if (statusMessageId is not null)
            changed.StatusMessageId = statusMessageId;
        if (lastDeliveredText is not null)
            changed.LastDeliveredText = lastDeliveredText;
        if (segmentsDelivered.HasValue)
            changed.SegmentsDelivered = segmentsDelivered.Value;
        if (totalSegments.HasValue)
            changed.TotalSegments = totalSegments.Value;
        if (lastErrorCode is not null)
            changed.LastErrorCode = lastErrorCode;
        if (lastErrorMessage is not null)
            changed.LastErrorMessage = lastErrorMessage;

        await PersistDomainEventAsync(changed);
    }

    private async Task<LarkTextFallbackTimedOperationResult<T>> ExecuteLarkCardTextFallbackOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string timeoutCode)
    {
        using var timeout = new CancellationTokenSource(LarkCardTextFallbackOperationTimeout, _timeProvider);
        try
        {
            return LarkTextFallbackTimedOperationResult<T>.Completed(
                await operation(timeout.Token).WaitAsync(
                    LarkCardTextFallbackOperationTimeout,
                    _timeProvider));
        }
        catch (Exception ex) when (
            ex is TimeoutException ||
            ex is OperationCanceledException && timeout.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Lark card text fallback operation timed out. runId={RunId} timeoutCode={TimeoutCode}",
                State.RunId,
                timeoutCode);
            return LarkTextFallbackTimedOperationResult<T>.FromTimeout(timeoutCode);
        }
    }

    private static AgentRunGAgentState ApplyLarkCardTextFallbackChanged(
        AgentRunGAgentState current,
        AgentRunLarkCardTextFallbackChangedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.LarkCardTextFallback ??= new AgentRunLarkCardTextFallbackState();
        var state = next.LarkCardTextFallback;
        state.CorrelationId = evt.CorrelationId ?? string.Empty;
        state.Phase = NormalizeLarkCardTextFallbackPhase(evt.Phase);
        state.UpdatedAtUnixMs = evt.ChangedAtUnixMs;
        if (evt.HasStatusMessageId)
            state.StatusMessageId = evt.StatusMessageId ?? string.Empty;
        if (evt.HasLastDeliveredText)
            state.LastDeliveredText = evt.LastDeliveredText ?? string.Empty;
        if (evt.HasSegmentsDelivered)
            state.SegmentsDelivered = evt.SegmentsDelivered;
        if (evt.HasTotalSegments)
            state.TotalSegments = evt.TotalSegments;
        if (evt.HasLastErrorCode)
            state.LastErrorCode = evt.LastErrorCode ?? string.Empty;
        if (evt.HasLastErrorMessage)
            state.LastErrorMessage = evt.LastErrorMessage ?? string.Empty;
        return next;
    }

    private static AgentRunLarkCardTextFallbackPhase NormalizeLarkCardTextFallbackPhase(
        AgentRunLarkCardTextFallbackPhase? phase) =>
        phase is null or AgentRunLarkCardTextFallbackPhase.Unspecified
            ? AgentRunLarkCardTextFallbackPhase.Idle
            : phase.Value;

    private static bool IsTerminalLarkCardTextFallbackPhase(
        AgentRunLarkCardTextFallbackPhase? phase) =>
        NormalizeLarkCardTextFallbackPhase(phase) is AgentRunLarkCardTextFallbackPhase.Completed
                                               or AgentRunLarkCardTextFallbackPhase.Failed;

    private bool TryBuildLarkTextFallbackContext(
        ChatActivity activity,
        out string nyxUserAccessToken,
        out string providerSlug,
        out LarkReceiveTarget primaryTarget,
        out LarkReceiveTarget? fallbackTarget,
        out string failureCode,
        out string failureMessage)
    {
        var extras = activity.TransportExtras;
        nyxUserAccessToken = NormalizeOptional(extras?.NyxUserAccessToken) ?? string.Empty;
        providerSlug = NormalizeOptional(extras?.NyxProviderSlug) ?? string.Empty;
        primaryTarget = default;
        fallbackTarget = null;
        failureCode = string.Empty;
        failureMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(nyxUserAccessToken))
        {
            failureCode = LarkCardTextFallbackPolicy.AccessTokenUnavailableErrorCode;
            failureMessage = "NyxUserAccessToken is required for Lark card text fallback delivery.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(providerSlug))
        {
            failureCode = "lark_provider_slug_unavailable";
            failureMessage = "NyxProviderSlug is required for Lark card text fallback delivery.";
            return false;
        }

        var target = LarkConversationTargets.BuildFromInboundWithFallback(
            ResolveLarkChatType(activity.Conversation),
            NormalizeOptional(extras?.NyxConversationId) ?? activity.Conversation?.CanonicalKey,
            extras?.NyxSenderUserId,
            extras?.NyxLarkUnionId,
            extras?.NyxLarkChatId);
        if (string.IsNullOrWhiteSpace(target.Primary.ReceiveId) ||
            string.IsNullOrWhiteSpace(target.Primary.ReceiveIdType))
        {
            failureCode = "lark_receive_target_unavailable";
            failureMessage = "Lark receive target is unavailable for text fallback delivery.";
            return false;
        }

        primaryTarget = target.Primary;
        fallbackTarget = target.Fallback;
        return true;
    }

    private static string ResolveLarkChatType(ConversationReference? conversation) =>
        conversation?.Scope switch
        {
            ConversationScope.DirectMessage => "p2p",
            ConversationScope.Group => "group",
            ConversationScope.Channel => "channel",
            ConversationScope.Thread => "thread",
            _ => string.Empty,
        };

    private LlmReplyDeliveryFailedEvent BuildLarkTextFallbackFailure(
        string correlationId,
        string errorCode,
        string errorMessage) =>
        new()
        {
            CorrelationId = correlationId,
            RunId = State.RunId ?? string.Empty,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
        };

    private static string BuildLarkFallbackFailureCode(string prefix, int? larkCode) =>
        larkCode.HasValue
            ? $"{prefix}:{larkCode.Value}"
            : prefix;

    private static string AppendFailureDetail(string firstCode, string firstDetail, string secondDetail) =>
        $"{firstCode}={firstDetail}; send={secondDetail}";

    private ILarkOutboundDispatcher? ResolveLarkOutboundDispatcher() =>
        Services.GetService<ILarkOutboundDispatcher>();

    private ILarkTextMessageEditPort? ResolveLarkTextMessageEditPort() =>
        Services.GetService<ILarkTextMessageEditPort>();

    private sealed record LarkTextFallbackPersistedFinalDelivery(
        bool Succeeded,
        bool AlreadyCompleted,
        string StatusMessageId,
        string VisibleText,
        string ErrorCode,
        string ErrorMessage)
    {
        public static LarkTextFallbackPersistedFinalDelivery Completed(
            string statusMessageId,
            string visibleText,
            bool alreadyCompleted) =>
            new(true, alreadyCompleted, statusMessageId, visibleText, string.Empty, string.Empty);

        public static LarkTextFallbackPersistedFinalDelivery Failed(
            string statusMessageId,
            string visibleText,
            string errorCode,
            string errorMessage) =>
            new(false, false, statusMessageId, visibleText, errorCode, errorMessage);
    }

    private sealed record LarkTextFallbackTimedOperationResult<T>(
        bool TimedOut,
        string TimeoutCode,
        T Value)
    {
        public static LarkTextFallbackTimedOperationResult<T> Completed(T value) =>
            new(false, string.Empty, value);

        public static LarkTextFallbackTimedOperationResult<T> FromTimeout(string timeoutCode) =>
            new(true, timeoutCode, default!);
    }

    private sealed record LarkTextFallbackSegmentSendResult(
        bool Succeeded,
        string MessageId,
        string ErrorCode,
        int? LarkCode,
        string Detail,
        bool ExceptionThrown)
    {
        public static LarkTextFallbackSegmentSendResult Sent(string messageId) =>
            new(true, messageId, string.Empty, null, string.Empty, false);

        public static LarkTextFallbackSegmentSendResult Failed(
            string errorCode,
            int? larkCode,
            string detail,
            bool exceptionThrown) =>
            new(false, string.Empty, errorCode, larkCode, detail, exceptionThrown);
    }

    private sealed record LarkTextFallbackFinalDelivery(
        bool Succeeded,
        int SegmentsDelivered,
        string LastDeliveredText,
        string ErrorCode,
        string ErrorMessage)
    {
        public static LarkTextFallbackFinalDelivery Completed(
            int segmentsDelivered,
            string lastDeliveredText) =>
            new(true, segmentsDelivered, lastDeliveredText, string.Empty, string.Empty);

        public static LarkTextFallbackFinalDelivery Failed(
            int segmentsDelivered,
            string lastDeliveredText,
            string errorCode,
            string errorMessage) =>
            new(false, segmentsDelivered, lastDeliveredText, errorCode, errorMessage);
    }

    private Task StartLarkCardCreateOperationAsync(
        LlmReplyCardStreamChunkEvent evt,
        string correlationId,
        string streamingElementId,
        long sequence,
        long generation)
    {
        var step = ResolveLarkCardReplyStreamRenderer().CreateCreateStep(
            new LarkCardCreateOperationStepInput(
                evt,
                correlationId,
                streamingElementId,
                sequence,
                generation));
        return PublishReplyOperationStepAsync(step, CancellationToken.None);
    }

    private Task StartLarkCardStreamOperationAsync(
        LlmReplyCardStreamChunkEvent evt,
        string correlationId,
        LarkCardDeliveryRuntimeState state,
        long sequence,
        long generation)
    {
        var step = ResolveLarkCardReplyStreamRenderer().CreateStreamStep(
            new LarkCardStreamOperationStepInput(
                evt,
                correlationId,
                state.CardId ?? string.Empty,
                state.StreamingElementId,
                sequence,
                generation));
        return PublishReplyOperationStepAsync(step, CancellationToken.None);
    }

    private Task StartLarkCardFinalizeOperationAsync(
        ChatActivity activityForToken,
        string correlationId,
        string commandId,
        LarkCardDeliveryRuntimeState state,
        string finalText,
        bool finalDiffers,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory,
        long sequence,
        long generation)
    {
        var step = ResolveLarkCardReplyStreamRenderer().CreateFinalizeStep(
            new LarkCardFinalizeOperationStepInput(
                activityForToken,
                correlationId,
                commandId,
                finalText,
                state.LastFlushedText,
                state.CardId ?? string.Empty,
                state.CardMessageId ?? string.Empty,
                state.StreamingElementId,
                finalDiffers,
                appendedHistory,
                sequence,
                generation));
        return PublishReplyOperationStepAsync(step, CancellationToken.None);
    }

    private Task PublishReplyOperationStepAsync(ReplyOperationStepEvent step, CancellationToken ct) =>
        SendToAsync(Id, step, ct);

    private async Task ScheduleLarkCardOperationTimeoutAsync(
        string correlationId,
        LarkCardOperationPhase operation,
        long sequence,
        long generation,
        string? cardId,
        string? cardMessageId,
        string? commandId,
        ChatActivity? activity,
        string? finalText,
        string? lastFlushedText,
        CancellationToken ct)
    {
        if (_callbackScheduler is null)
            return;

        await _callbackScheduler.ScheduleTimeoutAsync(
            BuildTimeoutRequest(
                BuildLarkCardOperationTimeoutCallbackId(correlationId, operation, generation),
                LarkCardOperationTimeout,
                new LarkCardOperationTimeoutFiredEvent
                {
                    CorrelationId = correlationId,
                    Operation = operation,
                    Sequence = sequence,
                    OperationGeneration = generation,
                    CardId = cardId ?? string.Empty,
                    CardMessageId = cardMessageId ?? string.Empty,
                    CommandId = commandId ?? string.Empty,
                    Activity = CloneForDurableState(activity),
                    FinalText = finalText ?? string.Empty,
                    LastFlushedText = lastFlushedText ?? string.Empty,
                    FiredAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                }),
            ct: ct);
    }

    private LarkCardDeliveryRuntimeState GetOrInitLarkCardDeliveryState()
    {
        var state = State.LarkCardDelivery;
        if (state is null)
            return LarkCardDeliveryRuntimeState.Initial;

        return new LarkCardDeliveryRuntimeState(
            NormalizeLarkCardDeliveryPhase(state.Phase),
            NormalizeOptional(state.CardId),
            NormalizeOptional(state.CardMessageId),
            NormalizeOptional(state.OriginalCardId),
            state.LastFlushedText ?? string.Empty,
            state.Sequence,
            NormalizeOptional(state.StreamingElementId) ?? LarkCardDeliveryRuntimeState.DefaultStreamingElementId,
            NormalizeOptional(state.TerminalReason),
            state.InFlightOperation == LarkCardOperationPhase.Unspecified
                ? null
                : new LarkCardOperationInFlight(
                    state.InFlightOperation,
                    state.InFlightSequence,
                    state.OperationGeneration),
            state.OperationGeneration,
            NormalizeOptional(state.PendingAccumulatedText),
            NormalizeOptional(state.PendingFinalizeText),
            NormalizeOptional(state.PendingFinalizeCommandId),
            state.PendingAppendedHistory.Select(entry => entry.Clone()).ToArray());
    }

    private async Task<LarkCardDeliveryRuntimeState> TransitionLarkCardDeliveryPhaseAsync(
        string correlationId,
        LarkCardDeliveryRuntimeState current,
        AgentRunLarkCardDeliveryPhase next,
        string? terminalReason = null,
        Func<LarkCardDeliveryRuntimeState, LarkCardDeliveryRuntimeState>? fieldUpdate = null)
    {
        next = NormalizeLarkCardDeliveryPhase(next);
        if (!IsLegalLarkCardDeliveryTransition(current.Phase, next))
        {
            _logger.LogWarning(
                "Illegal Lark card delivery phase transition {From}->{To} for runId={RunId} correlation={CorrelationId}; keeping current state",
                current.Phase,
                next,
                State.RunId,
                correlationId);
            return current;
        }

        var carried = fieldUpdate?.Invoke(current) ?? current;
        var updated = carried with
        {
            Phase = next,
            InFlight = IsTerminalLarkCardDeliveryPhase(next) ? null : carried.InFlight,
            PendingAccumulatedText = IsTerminalLarkCardDeliveryPhase(next) ? null : carried.PendingAccumulatedText,
            PendingFinalizeText = IsTerminalLarkCardDeliveryPhase(next) ? null : carried.PendingFinalizeText,
            PendingFinalizeCommandId = IsTerminalLarkCardDeliveryPhase(next) ? null : carried.PendingFinalizeCommandId,
            PendingAppendedHistory = IsTerminalLarkCardDeliveryPhase(next) ? [] : carried.PendingAppendedHistory,
            TerminalReason = IsTerminalLarkCardDeliveryPhase(next)
                ? (terminalReason ?? carried.TerminalReason)
                : carried.TerminalReason,
        };
        var changedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var changed = ToLarkCardDeliveryChangedEvent(correlationId, current, updated, changedAtUnixMs);
        changed.RunId = State.RunId ?? string.Empty;
        await PersistDomainEventAsync(changed);
        return updated;
    }

    private Task<LarkCardDeliveryRuntimeState> PersistLarkCardCoalescedStateAsync(
        string correlationId,
        LarkCardDeliveryRuntimeState state,
        string? accumulatedText = null,
        string? finalizeText = null,
        string? finalizeCommandId = null,
        IEnumerable<ConversationHistoryEntry>? appendedHistory = null) =>
        TransitionLarkCardDeliveryPhaseAsync(
            correlationId,
            state,
            state.Phase,
            fieldUpdate: s => s with
            {
                PendingAccumulatedText = NormalizeOptional(accumulatedText) ?? s.PendingAccumulatedText,
                PendingFinalizeText = NormalizeOptional(finalizeText) ?? s.PendingFinalizeText,
                PendingFinalizeCommandId = NormalizeOptional(finalizeCommandId) ?? s.PendingFinalizeCommandId,
                PendingAppendedHistory = appendedHistory is null
                    ? s.PendingAppendedHistory
                    : appendedHistory.Select(entry => entry.Clone()).ToArray(),
            });

    private static AgentRunLarkCardDeliveryChangedEvent ToLarkCardDeliveryChangedEvent(
        string correlationId,
        LarkCardDeliveryRuntimeState current,
        LarkCardDeliveryRuntimeState updated,
        long changedAtUnixMs)
    {
        var evt = new AgentRunLarkCardDeliveryChangedEvent
        {
            CorrelationId = correlationId,
            ChangedAtUnixMs = changedAtUnixMs,
            PreviousPhase = current.Phase,
            Phase = updated.Phase,
        };

        if (!string.Equals(current.CardId, updated.CardId, StringComparison.Ordinal))
            evt.CardIdAssigned = updated.CardId ?? string.Empty;
        if (!string.Equals(current.CardMessageId, updated.CardMessageId, StringComparison.Ordinal))
            evt.CardMessageIdAssigned = updated.CardMessageId ?? string.Empty;
        if (!string.Equals(current.OriginalCardId, updated.OriginalCardId, StringComparison.Ordinal))
            evt.OriginalCardIdAssigned = updated.OriginalCardId ?? string.Empty;
        if (!string.Equals(current.LastFlushedText, updated.LastFlushedText, StringComparison.Ordinal))
            evt.FlushedText = updated.LastFlushedText ?? string.Empty;
        if (current.Sequence != updated.Sequence)
            evt.Sequence = updated.Sequence;
        if (!string.Equals(current.StreamingElementId, updated.StreamingElementId, StringComparison.Ordinal))
            evt.StreamingElementId = updated.StreamingElementId ?? LarkCardDeliveryRuntimeState.DefaultStreamingElementId;
        if (!string.Equals(current.TerminalReason, updated.TerminalReason, StringComparison.Ordinal))
            evt.TerminalReason = updated.TerminalReason ?? string.Empty;

        var currentOperation = current.InFlight?.Operation ?? LarkCardOperationPhase.Unspecified;
        var updatedOperation = updated.InFlight?.Operation ?? LarkCardOperationPhase.Unspecified;
        if (currentOperation != updatedOperation)
            evt.InFlightOperation = updatedOperation;

        var currentSequence = current.InFlight?.Sequence ?? 0;
        var updatedSequence = updated.InFlight?.Sequence ?? 0;
        if (currentSequence != updatedSequence)
            evt.OperationSequence = updatedSequence;

        if (current.OperationGeneration != updated.OperationGeneration ||
            currentOperation != updatedOperation ||
            currentSequence != updatedSequence)
        {
            evt.OperationGeneration = updated.OperationGeneration;
        }
        if (!string.Equals(current.PendingAccumulatedText, updated.PendingAccumulatedText, StringComparison.Ordinal))
            evt.QueuedAccumulatedText = updated.PendingAccumulatedText ?? string.Empty;
        if (!string.Equals(current.PendingFinalizeText, updated.PendingFinalizeText, StringComparison.Ordinal))
            evt.FinalizeText = updated.PendingFinalizeText ?? string.Empty;
        if (!string.Equals(current.PendingFinalizeCommandId, updated.PendingFinalizeCommandId, StringComparison.Ordinal))
            evt.FinalizeCommandId = updated.PendingFinalizeCommandId ?? string.Empty;
        if (!HistoryEntriesEqual(current.PendingAppendedHistory, updated.PendingAppendedHistory))
            evt.AppendedHistory.AddRange(updated.PendingAppendedHistory.Select(entry => entry.Clone()));

        return evt;
    }

    private static AgentRunGAgentState ApplyLarkCardDeliveryChanged(
        AgentRunGAgentState current,
        AgentRunLarkCardDeliveryChangedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.LarkCardDelivery ??= new AgentRunLarkCardDeliveryState();
        var state = next.LarkCardDelivery;

        if (evt.Phase != AgentRunLarkCardDeliveryPhase.Unspecified)
            state.Phase = evt.Phase;
        if (evt.HasCardIdAssigned)
            state.CardId = evt.CardIdAssigned ?? string.Empty;
        if (evt.HasCardMessageIdAssigned)
            state.CardMessageId = evt.CardMessageIdAssigned ?? string.Empty;
        if (evt.HasOriginalCardIdAssigned)
            state.OriginalCardId = evt.OriginalCardIdAssigned ?? string.Empty;
        if (evt.HasFlushedText)
            state.LastFlushedText = evt.FlushedText ?? string.Empty;
        if (evt.HasSequence)
            state.Sequence = evt.Sequence;
        if (evt.HasStreamingElementId)
            state.StreamingElementId = evt.StreamingElementId ?? string.Empty;
        if (evt.HasTerminalReason)
            state.TerminalReason = evt.TerminalReason ?? string.Empty;
        if (evt.HasInFlightOperation)
            state.InFlightOperation = evt.InFlightOperation;
        if (evt.HasOperationSequence)
            state.InFlightSequence = evt.OperationSequence;
        if (evt.HasOperationGeneration)
            state.OperationGeneration = evt.OperationGeneration;
        if (evt.HasQueuedAccumulatedText)
            state.PendingAccumulatedText = evt.QueuedAccumulatedText ?? string.Empty;
        if (evt.HasFinalizeText)
            state.PendingFinalizeText = evt.FinalizeText ?? string.Empty;
        if (evt.HasFinalizeCommandId)
            state.PendingFinalizeCommandId = evt.FinalizeCommandId ?? string.Empty;
        if (evt.AppendedHistory.Count > 0)
        {
            state.PendingAppendedHistory.Clear();
            state.PendingAppendedHistory.AddRange(evt.AppendedHistory.Select(entry => entry.Clone()));
        }
        return next;
    }

    bool IReplyOperationActorContext.MatchesNyxRelayTextInFlight(
        string correlationId,
        NyxRelayTextOperationKind operation,
        long sequence,
        long generation) =>
        false;

    bool IReplyOperationActorContext.MatchesLarkCardInFlight(
        string correlationId,
        LarkCardOperationPhase operation,
        long sequence,
        long generation,
        string? cardId)
    {
        if (!IsCurrentCardDeliverySignal(State.RunId, correlationId))
            return false;

        var state = GetOrInitLarkCardDeliveryState();
        return MatchesLarkCardInFlight(state, operation, sequence, generation, cardId);
    }

    ConversationTurnRuntimeContext IReplyOperationActorContext.BuildNyxRelayRuntimeContext(
        string? correlationId,
        ChatActivity? activity,
        string? replyToken,
        long replyTokenExpiresAtUnixMs) =>
        BuildNyxRelayRuntimeContext(correlationId, activity, replyToken, replyTokenExpiresAtUnixMs);

    void IReplyOperationActorContext.RestoreRuntimeTransportCredentials(
        ChatActivity? activity,
        ConversationTurnRuntimeContext runtimeContext) =>
        RestoreRuntimeTransportCredentials(activity, runtimeContext);

    public Task DispatchReplyOperationCompletionAsync(
        IMessage evt,
        string correlationId,
        string operationName,
        CancellationToken ct) =>
        SendToAsync(Id, evt, ct);

    private bool IsCurrentCardDeliverySignal(string? runId, string? correlationId)
    {
        if (IsTerminal())
            return false;

        if (!string.IsNullOrWhiteSpace(State.RunId) &&
            !string.IsNullOrWhiteSpace(runId) &&
            !string.Equals(State.RunId, runId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(State.CorrelationId) &&
            !string.Equals(State.CorrelationId, correlationId, StringComparison.Ordinal))
        {
            return false;
        }

        return State.Status is AgentRunStatus.Started
               or AgentRunStatus.ReplyGenerationRequested
               or AgentRunStatus.ReplyProduced;
    }

    private static bool MatchesLarkCardInFlight(
        LarkCardDeliveryRuntimeState state,
        LarkCardOperationPhase operation,
        long sequence,
        long generation,
        string? cardId = null)
    {
        if (state.InFlight is not { } inFlight)
            return false;
        if (inFlight.Operation != operation ||
            inFlight.Sequence != sequence ||
            inFlight.Generation != generation)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(cardId) &&
            !string.Equals(state.CardId, cardId, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private static bool ShouldSkipLarkCardStreamingForUnavailable(
        LarkCardDeliveryRuntimeState state,
        LarkCardDeliveryGuardSource source) =>
        source switch
        {
            LarkCardDeliveryGuardSource.AcceptInterimChunk => !state.AllowsInterimEdit,
            LarkCardDeliveryGuardSource.Finalize => !state.AllowsFinalize,
            _ => false,
        };

    private static bool IsTerminalLarkCardDeliveryPhase(AgentRunLarkCardDeliveryPhase phase) =>
        phase is AgentRunLarkCardDeliveryPhase.Completed
              or AgentRunLarkCardDeliveryPhase.Aborted
              or AgentRunLarkCardDeliveryPhase.Terminated
              or AgentRunLarkCardDeliveryPhase.CreationFailed;

    private static bool IsLegalLarkCardDeliveryTransition(
        AgentRunLarkCardDeliveryPhase from,
        AgentRunLarkCardDeliveryPhase to)
    {
        from = NormalizeLarkCardDeliveryPhase(from);
        to = NormalizeLarkCardDeliveryPhase(to);
        if (from == to && from is AgentRunLarkCardDeliveryPhase.Creating
                         or AgentRunLarkCardDeliveryPhase.Streaming)
        {
            return true;
        }

        return (from, to) switch
        {
            (AgentRunLarkCardDeliveryPhase.Idle, AgentRunLarkCardDeliveryPhase.Creating) => true,
            (AgentRunLarkCardDeliveryPhase.Creating, AgentRunLarkCardDeliveryPhase.Streaming) => true,
            (AgentRunLarkCardDeliveryPhase.Creating, AgentRunLarkCardDeliveryPhase.CreationFailed) => true,
            (AgentRunLarkCardDeliveryPhase.Creating, AgentRunLarkCardDeliveryPhase.Terminated) => true,
            (AgentRunLarkCardDeliveryPhase.Streaming, AgentRunLarkCardDeliveryPhase.Completed) => true,
            (AgentRunLarkCardDeliveryPhase.Streaming, AgentRunLarkCardDeliveryPhase.Aborted) => true,
            (AgentRunLarkCardDeliveryPhase.Streaming, AgentRunLarkCardDeliveryPhase.Terminated) => true,
            _ => false,
        };
    }

    private static AgentRunLarkCardDeliveryPhase NormalizeLarkCardDeliveryPhase(
        AgentRunLarkCardDeliveryPhase phase) =>
        phase == AgentRunLarkCardDeliveryPhase.Unspecified
            ? AgentRunLarkCardDeliveryPhase.Idle
            : phase;

    private long NextLarkCardOperationGeneration(LarkCardDeliveryRuntimeState state) =>
        Math.Max(state.OperationGeneration, state.InFlight?.Generation ?? 0) + 1;

    private ILarkCardReplyStreamRenderer ResolveLarkCardReplyStreamRenderer() =>
        Services.GetService<ILarkCardReplyStreamRenderer>() ??
        new LarkCardReplyStreamRenderer(
            ResolveCardRunner(),
            NullLogger<LarkCardReplyStreamRenderer>.Instance);

    private IConversationCardTurnRunner ResolveCardRunner() =>
        Services.GetService<IConversationCardTurnRunner>() ?? new NullConversationCardTurnRunner();

    private ConversationTurnRuntimeContext BuildNyxRelayRuntimeContext(
        string? correlationId,
        ChatActivity? activity,
        string? replyToken = null,
        long replyTokenExpiresAtUnixMs = 0)
    {
        var normalizedCorrelationId = NormalizeOptional(activity?.OutboundDelivery?.CorrelationId) ??
                                      NormalizeOptional(correlationId);
        var normalizedReplyToken = NormalizeOptional(replyToken);
        var replyMessageId = NormalizeOptional(activity?.OutboundDelivery?.ReplyMessageId);
        var accessToken = NormalizeOptional(activity?.TransportExtras?.NyxUserAccessToken);
        if (normalizedCorrelationId is null || normalizedReplyToken is null || replyMessageId is null)
            return new ConversationTurnRuntimeContext(null, accessToken);

        var expiresAt = replyTokenExpiresAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(replyTokenExpiresAtUnixMs)
            : _timeProvider.GetUtcNow().AddMinutes(30);
        if (expiresAt <= _timeProvider.GetUtcNow())
            return new ConversationTurnRuntimeContext(null, accessToken);

        return new ConversationTurnRuntimeContext(
            new NyxRelayReplyTokenContext(
                normalizedCorrelationId,
                normalizedReplyToken,
                replyMessageId,
                expiresAt,
                accessToken),
            accessToken);
    }

    private static void RestoreRuntimeTransportCredentials(
        ChatActivity? activity,
        ConversationTurnRuntimeContext runtimeContext)
    {
        var accessToken = NormalizeOptional(runtimeContext.NyxUserAccessToken);
        if (activity is null || accessToken is null)
            return;

        activity.TransportExtras ??= new TransportExtras();
        activity.TransportExtras.NyxUserAccessToken = accessToken;
    }

    private static ChatActivity? CloneForDurableState(ChatActivity? activity)
    {
        if (activity is null)
            return null;

        var durable = activity.Clone();
        if (durable.TransportExtras is not null)
            durable.TransportExtras.NyxUserAccessToken = string.Empty;
        return durable;
    }

    private static string BuildLlmReplyCommandId(string? correlationId) =>
        $"llm:{correlationId?.Trim() ?? string.Empty}";

    private static string BuildLarkCardOperationTimeoutCallbackId(
        string correlationId,
        LarkCardOperationPhase operation,
        long generation) =>
        $"agent-run-lark-card:{correlationId}:{operation}:{generation}";

    private static ConversationCardCreateResult ToCreateResult(LarkCardOperationCompletedEvent evt)
    {
        var raw = evt.RawResult ?? new LarkCardOperationRawResult();
        if (evt.State == LarkCardOperationResultState.Succeeded)
            return ConversationCardCreateResult.Succeeded(raw.CardId, raw.CardMessageId);

        if (evt.State == LarkCardOperationResultState.Faulted)
            return ConversationCardCreateResult.Failed(
                BuildFaultErrorCode(LarkCardOperationPhase.Create, raw),
                raw.ExceptionMessage);

        return raw.IsPostSendFailure
            ? ConversationCardCreateResult.PostSendFailed(
                raw.CardId,
                raw.CardMessageId,
                raw.RawErrorCode,
                raw.RawErrorSummary,
                raw.IsRateLimited,
                raw.IsTableLimitExceeded,
                raw.IsCardUnavailable)
            : ConversationCardCreateResult.Failed(
                raw.RawErrorCode,
                raw.RawErrorSummary,
                raw.IsRateLimited,
                raw.IsTableLimitExceeded,
                raw.IsCardUnavailable);
    }

    private static ConversationCardStreamResult ToStreamResult(LarkCardOperationCompletedEvent evt)
    {
        var raw = evt.RawResult ?? new LarkCardOperationRawResult();
        if (evt.State == LarkCardOperationResultState.Succeeded)
            return ConversationCardStreamResult.Succeeded();

        return ConversationCardStreamResult.Failed(
            evt.State == LarkCardOperationResultState.Faulted
                ? BuildFaultErrorCode(LarkCardOperationPhase.Stream, raw)
                : raw.RawErrorCode,
            evt.State == LarkCardOperationResultState.Faulted
                ? raw.ExceptionMessage
                : raw.RawErrorSummary,
            raw.IsRateLimited,
            raw.IsTableLimitExceeded,
            raw.IsCardUnavailable);
    }

    private static ConversationCardFinalizeResult ToFinalizeResult(LarkCardOperationCompletedEvent evt)
    {
        var raw = evt.RawResult ?? new LarkCardOperationRawResult();
        if (evt.State == LarkCardOperationResultState.Succeeded)
            return ConversationCardFinalizeResult.Succeeded();

        return ConversationCardFinalizeResult.Failed(
            evt.State == LarkCardOperationResultState.Faulted
                ? BuildFaultErrorCode(LarkCardOperationPhase.Finalize, raw)
                : raw.RawErrorCode,
            evt.State == LarkCardOperationResultState.Faulted
                ? raw.ExceptionMessage
                : raw.RawErrorSummary,
            raw.FinalTextWritten);
    }

    private static string BuildFaultErrorCode(
        LarkCardOperationPhase operation,
        LarkCardOperationRawResult raw)
    {
        var operationName = operation switch
        {
            LarkCardOperationPhase.Create => "create",
            LarkCardOperationPhase.Stream => "stream",
            LarkCardOperationPhase.Finalize => "finalize",
            _ => "unknown",
        };
        var exceptionType = string.IsNullOrWhiteSpace(raw.ExceptionType)
            ? "Exception"
            : raw.ExceptionType;
        return $"{operationName}_threw:{exceptionType}";
    }

    private static string TrimLogValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength] + "...";
    }

    private static bool HistoryEntriesEqual(
        IReadOnlyList<ConversationHistoryEntry> left,
        IReadOnlyList<ConversationHistoryEntry> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    }
}
