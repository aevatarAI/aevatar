using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ConversationGAgent
{
    // Refactor (iter20/cluster-004):
    //   Old pattern: ConversationGAgent 持有 actor token registry + 可见回复状态部分仅在内存
    //   New principle: 删 actor token registry,credentials runtime-only,可见回复 lifecycle 持久到 ConversationGAgent state
    // Refactor (iter107/cluster-1-channel-business-io-process-queue):
    //   Old pattern: process-local Channel/Task workers owned business IO via singleton executor.
    //   New principle: actor-owned operation state (operation_id/lease_epoch/step) + typed self-continuation events; provider IO is inline async, no in-process worker queue.
    /// <summary>
    /// Per-turn phase of the Lark CardKit streaming pipeline. Distinct from
    /// <see cref="NyxRelayStreamingPhase"/> (which models channel-relay edit-message
    /// streaming): card streaming has its own lifecycle (allocate card entity, bind to
    /// chat, stream element content, close streaming mode) and goes through the API-key
    /// proxy directly rather than channel-relay's <c>/reply{,/update}</c> surface.
    /// </summary>
    /// <remarks>
    /// Fallback semantics: Lark CardKit streaming is the primary production Lark path.
    /// Message edit is only a fallback for card creation/pre-send failure or explicitly
    /// non-CardKit deployments; do not roll production Lark back from CardKit to message
    /// edit as an incident mitigation. Once <see cref="Streaming"/> is reached, the card
    /// path owns the turn — mid-stream rate-limit / table-limit failures terminate the
    /// turn at <see cref="Terminated"/> with the last flushed text persisted as partial.
    /// </remarks>
    private enum LarkCardStreamingPhase
    {
        Idle,
        Creating,
        Streaming,
        Completed,
        Aborted,
        Terminated,
        CreationFailed,
    }

    private enum LarkCardStreamingGuardSource
    {
        AcceptInterimChunk,
        Finalize,
    }

    private sealed record LarkCardOperationInFlight(
        LarkCardOperationPhase Operation,
        long Sequence,
        long Generation);

    /// <summary>
    /// Actor-scoped streaming state for one CardKit-driven turn, backed by
    /// ConversationGAgentState.ActiveReplyLifecycles.
    /// </summary>
    /// <param name="Phase">Lifecycle phase; gates interim updates and finalization.</param>
    /// <param name="CardId">
    /// CardKit card entity id returned by <c>cardkit/v1/cards</c>. Null until
    /// <see cref="LarkCardStreamingPhase.Streaming"/>; required for every element-content
    /// and settings update afterwards.
    /// </param>
    /// <param name="CardMessageId">
    /// Lark IM message id returned by the <c>im/v1/messages</c> send that bound the card
    /// to a chat. Used by the unavailable-guard to detect upstream message recall.
    /// </param>
    /// <param name="OriginalCardId">
    /// Preserved card id for terminal full-card update if mid-stream we fall back to text
    /// patch (table-limit class errors). Currently always equal to <see cref="CardId"/>;
    /// reserved for the mid-stream-fallback follow-up (#589 Scope D).
    /// </param>
    /// <param name="LastFlushedText">
    /// Last text successfully streamed into the card element. Persisted as the user-visible
    /// terminal state when finalization fails after streaming started.
    /// </param>
    /// <param name="Sequence">
    /// Monotonic counter passed to every CardKit write. Pre-incremented before each call;
    /// Lark rejects stale writes deterministically.
    /// </param>
    /// <param name="StreamingElementId">
    /// Element id within the card to stream into. Defaults to <c>streaming_main</c>;
    /// must match the card template's element naming.
    /// </param>
    /// <param name="TerminalReason">Diagnostic reason captured on entry to terminal phases.</param>
    private sealed record LarkCardStreamingState(
        LarkCardStreamingPhase Phase,
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

        public static LarkCardStreamingState Initial { get; } = new(
            LarkCardStreamingPhase.Idle,
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

        /// <summary>Phase permits accepting a new chunk (initial or interim).</summary>
        public bool AllowsInterimEdit =>
            Phase is LarkCardStreamingPhase.Idle
                  or LarkCardStreamingPhase.Streaming;

        /// <summary>
        /// Card creation already failed — dispatcher should route subsequent chunks to the
        /// text-edit sink for the rest of this turn.
        /// </summary>
        public bool AllowsTextEditFallback =>
            Phase is LarkCardStreamingPhase.Idle
                  or LarkCardStreamingPhase.CreationFailed;

        /// <summary>Phase permits attempting a finalize (close streaming + optional final update).</summary>
        public bool AllowsFinalize =>
            Phase is LarkCardStreamingPhase.Streaming;
    }

    private static bool IsTerminalLarkCardStreamingPhase(LarkCardStreamingPhase phase) =>
        phase is LarkCardStreamingPhase.Completed
              or LarkCardStreamingPhase.Aborted
              or LarkCardStreamingPhase.Terminated
              or LarkCardStreamingPhase.CreationFailed;

    private static bool IsLegalLarkCardStreamingTransition(LarkCardStreamingPhase from, LarkCardStreamingPhase to) =>
        (from, to) switch
        {
            (LarkCardStreamingPhase.Idle, LarkCardStreamingPhase.Creating) => true,

            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.Creating) => true,
            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.Streaming) => true,
            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.CreationFailed) => true,
            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.Terminated) => true,

            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Streaming) => true,
            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Completed) => true,
            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Aborted) => true,
            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Terminated) => true,

            _ => false,
        };

    // Refactor (iter20/cluster-004):
    //   Old pattern: Card streaming lifecycle lived in a process-memory dictionary beside the reply token registry.
    //   New principle: Rehydrate card lifecycle from actor-owned persisted state; keep credentials runtime-only.
    private LarkCardStreamingState GetOrInitLarkCardStreamingState(string correlationId)
    {
        var lifecycle = FindReplyLifecycle(correlationId, ConversationReplyLifecycleMode.LarkCard);
        if (lifecycle is null)
            return LarkCardStreamingState.Initial;

        return new LarkCardStreamingState(
            ToLarkCardStreamingPhase(lifecycle.Phase),
            NormalizeOptional(lifecycle.CardId),
            NormalizeOptional(lifecycle.CardMessageId),
            NormalizeOptional(lifecycle.OriginalCardId),
            lifecycle.LastFlushedText ?? string.Empty,
            lifecycle.Sequence,
            NormalizeOptional(lifecycle.StreamingElementId) ?? LarkCardStreamingState.DefaultStreamingElementId,
            NormalizeOptional(lifecycle.TerminalReason),
            lifecycle.LarkCardInFlightOperation == LarkCardOperationPhase.Unspecified
                ? null
                : new LarkCardOperationInFlight(
                    lifecycle.LarkCardInFlightOperation,
                    lifecycle.LarkCardInFlightSequence,
                    lifecycle.LarkCardOperationGeneration),
            lifecycle.LarkCardOperationGeneration,
            NormalizeOptional(lifecycle.PendingAccumulatedText),
            NormalizeOptional(lifecycle.PendingFinalizeText),
            NormalizeOptional(lifecycle.PendingFinalizeCommandId),
            lifecycle.PendingAppendedHistory.Select(entry => entry.Clone()).ToArray());
    }

    private static bool ShouldSkipLarkCardStreamingForUnavailable(
        LarkCardStreamingState state,
        LarkCardStreamingGuardSource source) =>
        source switch
        {
            LarkCardStreamingGuardSource.AcceptInterimChunk => !state.AllowsInterimEdit,
            LarkCardStreamingGuardSource.Finalize => !state.AllowsFinalize,
            _ => false,
        };

    // Refactor (iter80/cluster-081-channel-reply-lifecycle-event-state-schema):
    //   Old pattern: ConversationReplyLifecycleChangedEvent carried full ConversationReplyLifecycleState
    //   New principle: event describes transition facts; reducer derives current state from event + actor state
    private async Task<LarkCardStreamingState> TransitionLarkCardStreamingPhaseAsync(
        string correlationId,
        LarkCardStreamingState current,
        LarkCardStreamingPhase next,
        string? terminalReason = null,
        Func<LarkCardStreamingState, LarkCardStreamingState>? fieldUpdate = null)
    {
        if (!IsLegalLarkCardStreamingTransition(current.Phase, next))
        {
            Logger.LogWarning(
                "Illegal Lark card streaming phase transition {From}->{To} for correlation={CorrelationId}; keeping current state",
                current.Phase, next, correlationId);
            return current;
        }

        var carried = fieldUpdate?.Invoke(current) ?? current;
        var updated = carried with
        {
            Phase = next,
            InFlight = IsTerminalLarkCardStreamingPhase(next) ? null : carried.InFlight,
            PendingAccumulatedText = IsTerminalLarkCardStreamingPhase(next) ? null : carried.PendingAccumulatedText,
            PendingFinalizeText = IsTerminalLarkCardStreamingPhase(next) ? null : carried.PendingFinalizeText,
            PendingFinalizeCommandId = IsTerminalLarkCardStreamingPhase(next) ? null : carried.PendingFinalizeCommandId,
            PendingAppendedHistory = IsTerminalLarkCardStreamingPhase(next) ? [] : carried.PendingAppendedHistory,
            TerminalReason = IsTerminalLarkCardStreamingPhase(next)
                ? (terminalReason ?? carried.TerminalReason)
                : carried.TerminalReason,
        };
        var changedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await PersistDomainEventAsync(ToLifecycleChangedEvent(correlationId, current, updated, changedAtUnixMs));
        return updated;
    }

    private static LarkCardStreamingPhase ToLarkCardStreamingPhase(ConversationReplyLifecyclePhase phase) =>
        phase switch
        {
            ConversationReplyLifecyclePhase.LarkCardCreating => LarkCardStreamingPhase.Creating,
            ConversationReplyLifecyclePhase.LarkCardStreaming => LarkCardStreamingPhase.Streaming,
            ConversationReplyLifecyclePhase.LarkCardCompleted => LarkCardStreamingPhase.Completed,
            ConversationReplyLifecyclePhase.LarkCardAborted => LarkCardStreamingPhase.Aborted,
            ConversationReplyLifecyclePhase.LarkCardTerminated => LarkCardStreamingPhase.Terminated,
            ConversationReplyLifecyclePhase.LarkCardCreationFailed => LarkCardStreamingPhase.CreationFailed,
            _ => LarkCardStreamingPhase.Idle,
        };

    private static ConversationReplyLifecyclePhase ToLifecyclePhase(LarkCardStreamingPhase phase) =>
        phase switch
        {
            LarkCardStreamingPhase.Creating => ConversationReplyLifecyclePhase.LarkCardCreating,
            LarkCardStreamingPhase.Streaming => ConversationReplyLifecyclePhase.LarkCardStreaming,
            LarkCardStreamingPhase.Completed => ConversationReplyLifecyclePhase.LarkCardCompleted,
            LarkCardStreamingPhase.Aborted => ConversationReplyLifecyclePhase.LarkCardAborted,
            LarkCardStreamingPhase.Terminated => ConversationReplyLifecyclePhase.LarkCardTerminated,
            LarkCardStreamingPhase.CreationFailed => ConversationReplyLifecyclePhase.LarkCardCreationFailed,
            _ => ConversationReplyLifecyclePhase.Unspecified,
        };

    private static ConversationReplyLifecycleChangedEvent ToLifecycleChangedEvent(
        string correlationId,
        LarkCardStreamingState current,
        LarkCardStreamingState updated,
        long changedAtUnixMs)
    {
        var evt = new ConversationReplyLifecycleChangedEvent
        {
            CorrelationId = correlationId,
            Mode = ConversationReplyLifecycleMode.LarkCard,
            PreviousPhase = ToLifecyclePhase(current.Phase),
            Phase = ToLifecyclePhase(updated.Phase),
            ChangedAtUnixMs = changedAtUnixMs,
        };

        if (!string.Equals(current.CardId, updated.CardId, StringComparison.Ordinal))
            evt.CardIdAssigned = updated.CardId ?? string.Empty;
        if (!string.Equals(current.CardMessageId, updated.CardMessageId, StringComparison.Ordinal))
            evt.CardMessageIdAssigned = updated.CardMessageId ?? string.Empty;
        if (!string.Equals(current.OriginalCardId, updated.OriginalCardId, StringComparison.Ordinal))
            evt.OriginalCardIdAssigned = updated.OriginalCardId ?? string.Empty;
        if (!string.Equals(current.LastFlushedText, updated.LastFlushedText, StringComparison.Ordinal))
            evt.FlushedTextDelta = updated.LastFlushedText ?? string.Empty;
        if (current.Sequence != updated.Sequence)
            evt.SequenceDelta = updated.Sequence - current.Sequence;
        if (!string.Equals(current.StreamingElementId, updated.StreamingElementId, StringComparison.Ordinal))
            evt.StreamingElementIdSelected = updated.StreamingElementId ?? LarkCardStreamingState.DefaultStreamingElementId;
        if (!string.Equals(current.TerminalReason, updated.TerminalReason, StringComparison.Ordinal))
            evt.TerminalReason = updated.TerminalReason ?? string.Empty;

        var currentOperation = current.InFlight?.Operation ?? LarkCardOperationPhase.Unspecified;
        var updatedOperation = updated.InFlight?.Operation ?? LarkCardOperationPhase.Unspecified;
        if (currentOperation != updatedOperation)
            evt.LarkCardOperation = updatedOperation;

        var currentSequence = current.InFlight?.Sequence ?? 0;
        var updatedSequence = updated.InFlight?.Sequence ?? 0;
        if (currentSequence != updatedSequence)
            evt.OperationSequence = updatedSequence;

        if (current.OperationGeneration != updated.OperationGeneration ||
            currentOperation != updatedOperation ||
            currentSequence != updatedSequence)
            evt.OperationGeneration = updated.OperationGeneration;
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

    private IConversationCardTurnRunner ResolveCardRunner() =>
        Services.GetService<IConversationCardTurnRunner>() ?? new NullConversationCardTurnRunner();

    private long NextLarkCardOperationGeneration(LarkCardStreamingState state) =>
        Math.Max(state.OperationGeneration, state.InFlight?.Generation ?? 0) + 1;

    private static string BuildLarkCardOperationTimeoutCallbackId(
        string correlationId,
        LarkCardOperationPhase operation,
        long generation) =>
        $"conversation-lark-card:{correlationId}:{operation}:{generation}";

    private static string BuildLarkCardOperationId(
        string correlationId,
        LarkCardOperationPhase operation,
        long sequence,
        long generation) =>
        $"{correlationId}:{operation}:{sequence}:{generation}";

    private static EventEnvelope CreateLarkCardContinuationEnvelope(string actorId, IMessage evt, string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateDirect(actorId, actorId),
            Propagation = new EnvelopePropagation { CorrelationId = correlationId },
        };

    private async Task DispatchLarkCardContinuationAsync(IMessage evt, string correlationId, CancellationToken ct)
    {
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (dispatchPort is null)
        {
            Logger.LogWarning(
                "IActorDispatchPort unavailable; cannot dispatch Lark card continuation. correlation={CorrelationId}",
                correlationId);
            return;
        }

        await dispatchPort.DispatchAsync(Id, CreateLarkCardContinuationEnvelope(Id, evt, correlationId), ct)
            .ConfigureAwait(false);
    }

    private async Task ScheduleLarkCardOperationTimeoutAsync(
        string correlationId,
        LarkCardOperationPhase operation,
        long sequence,
        long generation,
        string? cardId,
        string? cardMessageId,
        string? commandId,
        LlmReplyCardStreamChunkEvent? chunk,
        ChatActivity? activity,
        string? finalText,
        string? lastFlushedText,
        CancellationToken ct)
    {
        // Refactor (iter73/cluster-073-durable-callback-runtime-credentials):
        //   Old pattern: durable callback envelope clones full command/chunk payload, may embed transient runtime credentials (reply_token)
        //   New principle: callback payload carries only stable IDs + actor-owned lease keys; actor reconciles from current actor state on fire
        await ScheduleSelfDurableTimeoutAsync(
            BuildLarkCardOperationTimeoutCallbackId(correlationId, operation, generation),
            StreamingFailureUpdateTimeout,
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
                FiredAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ct: ct);
    }

    private Task StartLarkCardCreateOperationAsync(
        LlmReplyCardStreamChunkEvent evt,
        string correlationId,
        string streamingElementId,
        long sequence,
        long generation)
    {
        var workItemId = BuildLarkCardOperationId(correlationId, LarkCardOperationPhase.Create, sequence, generation);
        return PublishReplyOperationStepAsync(
            workItemId,
            "lark-card-create",
            correlationId,
            generation,
            ReplyOperationStepEvent.PayloadOneofCase.LarkCard,
            new LarkCardOperationStepPayload
            {
                Operation = LarkCardOperationPhase.Create,
                Sequence = sequence,
                OperationGeneration = generation,
                Chunk = evt.Clone(),
                StreamingElementId = streamingElementId,
            },
            CancellationToken.None);
    }

    private async Task ExecuteLarkCardCreateOperationAsync(
        IConversationCardTurnRunner runner,
        LlmReplyCardStreamChunkEvent chunk,
        string correlationId,
        string streamingElementId,
        long sequence,
        long generation,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        LarkCardOperationCompletedEvent signal;
        try
        {
            var result = await runner.RunCardCreateAsync(
                    chunk,
                    streamingElementId,
                    runtimeContext,
                    ct)
                .ConfigureAwait(false);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = BuildLarkCardOperationId(correlationId, LarkCardOperationPhase.Create, sequence, generation),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Create,
                Sequence = sequence,
                OperationGeneration = generation,
                State = result.Success
                    ? LarkCardOperationResultState.Succeeded
                    : LarkCardOperationResultState.Failed,
                RawResult = ToRawResult(result),
                Chunk = chunk,
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Card create executor threw. correlation={CorrelationId}", correlationId);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = BuildLarkCardOperationId(correlationId, LarkCardOperationPhase.Create, sequence, generation),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Create,
                Sequence = sequence,
                OperationGeneration = generation,
                State = LarkCardOperationResultState.Faulted,
                RawResult = ToRawFault(ex),
                Chunk = chunk,
            };
        }

        await DispatchLarkCardContinuationAsync(signal, correlationId, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private Task StartLarkCardStreamOperationAsync(
        LlmReplyCardStreamChunkEvent evt,
        string correlationId,
        LarkCardStreamingState state,
        long sequence,
        long generation)
    {
        var cardId = state.CardId ?? string.Empty;
        var streamingElementId = state.StreamingElementId;
        var workItemId = BuildLarkCardOperationId(correlationId, LarkCardOperationPhase.Stream, sequence, generation);
        return PublishReplyOperationStepAsync(
            workItemId,
            "lark-card-stream",
            correlationId,
            generation,
            ReplyOperationStepEvent.PayloadOneofCase.LarkCard,
            new LarkCardOperationStepPayload
            {
                Operation = LarkCardOperationPhase.Stream,
                Sequence = sequence,
                OperationGeneration = generation,
                Chunk = evt.Clone(),
                CardId = cardId,
                StreamingElementId = streamingElementId,
            },
            CancellationToken.None);
    }

    private async Task ExecuteLarkCardStreamOperationAsync(
        IConversationCardTurnRunner runner,
        LlmReplyCardStreamChunkEvent chunk,
        string correlationId,
        string cardId,
        string streamingElementId,
        long sequence,
        long generation,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        LarkCardOperationCompletedEvent signal;
        try
        {
            var result = await runner.RunCardStreamAsync(
                    chunk,
                    cardId,
                    streamingElementId,
                    sequence,
                    runtimeContext,
                    ct)
                .ConfigureAwait(false);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = BuildLarkCardOperationId(correlationId, LarkCardOperationPhase.Stream, sequence, generation),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Stream,
                Sequence = sequence,
                OperationGeneration = generation,
                State = result.Success
                    ? LarkCardOperationResultState.Succeeded
                    : LarkCardOperationResultState.Failed,
                RawResult = ToRawResult(result),
                CardId = cardId,
                StreamingElementId = streamingElementId,
                Chunk = chunk,
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Card stream executor threw. correlation={CorrelationId}, seq={Sequence}", correlationId, sequence);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = BuildLarkCardOperationId(correlationId, LarkCardOperationPhase.Stream, sequence, generation),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Stream,
                Sequence = sequence,
                OperationGeneration = generation,
                State = LarkCardOperationResultState.Faulted,
                RawResult = ToRawFault(ex),
                CardId = cardId,
                StreamingElementId = streamingElementId,
                Chunk = chunk,
            };
        }

        await DispatchLarkCardContinuationAsync(signal, correlationId, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private Task StartLarkCardFinalizeOperationAsync(
        ChatActivity activityForToken,
        string correlationId,
        string commandId,
        LarkCardStreamingState state,
        string finalText,
        bool finalDiffers,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory,
        long sequence,
        long generation,
        ConversationTurnRuntimeContext runtimeContext)
    {
        var cardId = state.CardId ?? string.Empty;
        var cardMessageId = state.CardMessageId ?? string.Empty;
        var streamingElementId = state.StreamingElementId;
        var lastFlushedText = state.LastFlushedText;
        return ExecuteLarkCardFinalizeOperationAsync(
            ResolveCardRunner(),
            activityForToken.Clone(),
            correlationId,
            commandId,
            cardId,
            cardMessageId,
            streamingElementId,
            finalText,
            lastFlushedText,
            finalDiffers,
            appendedHistory.Select(entry => entry.Clone()).ToArray(),
            sequence,
            generation,
            runtimeContext,
            CancellationToken.None);
    }

    private async Task ExecuteLarkCardFinalizeOperationAsync(
        IConversationCardTurnRunner runner,
        ChatActivity activityForToken,
        string correlationId,
        string commandId,
        string cardId,
        string cardMessageId,
        string streamingElementId,
        string finalText,
        string lastFlushedText,
        bool finalDiffers,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory,
        long sequence,
        long generation,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        LarkCardOperationCompletedEvent signal;
        try
        {
            var result = await runner.RunCardFinalizeAsync(
                    activityForToken,
                    cardId,
                    streamingElementId,
                    finalText,
                    finalDiffers,
                    sequence,
                    runtimeContext,
                    ct)
                .ConfigureAwait(false);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = BuildLarkCardOperationId(correlationId, LarkCardOperationPhase.Finalize, sequence, generation),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Finalize,
                Sequence = sequence,
                OperationGeneration = generation,
                State = result.Success
                    ? LarkCardOperationResultState.Succeeded
                    : LarkCardOperationResultState.Failed,
                RawResult = ToRawResult(result),
                CardId = cardId,
                CardMessageId = cardMessageId,
                CommandId = commandId,
                Activity = CloneForDurableState(activityForToken) ?? new ChatActivity(),
                FinalText = finalText,
                LastFlushedText = lastFlushedText,
            };
            signal.AppendedHistory.AddRange(appendedHistory.Select(entry => entry.Clone()));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Card finalize executor threw. correlation={CorrelationId}", correlationId);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = BuildLarkCardOperationId(correlationId, LarkCardOperationPhase.Finalize, sequence, generation),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Finalize,
                Sequence = sequence,
                OperationGeneration = generation,
                State = LarkCardOperationResultState.Faulted,
                RawResult = ToRawFault(ex),
                CardId = cardId,
                CardMessageId = cardMessageId,
                CommandId = commandId,
                Activity = CloneForDurableState(activityForToken) ?? new ChatActivity(),
                FinalText = finalText,
                LastFlushedText = lastFlushedText,
            };
            signal.AppendedHistory.AddRange(appendedHistory.Select(entry => entry.Clone()));
        }

        await DispatchLarkCardContinuationAsync(signal, correlationId, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task ExecuteLarkCardOperationStepAsync(
        ReplyOperationStepEvent evt,
        LarkCardOperationStepPayload step)
    {
        var correlationId = evt.CorrelationId;
        var state = GetOrInitLarkCardStreamingState(correlationId);
        if (!MatchesLarkCardInFlight(
                state,
                step.Operation,
                step.Sequence,
                step.OperationGeneration,
                NormalizeOptional(step.CardId)))
        {
            return;
        }

        var runtimeContext = step.Operation == LarkCardOperationPhase.Finalize
            ? BuildNyxRelayRuntimeContext(
                correlationId,
                step.Activity,
                string.Empty,
                0)
            : BuildNyxRelayRuntimeContext(
                step.Chunk?.CorrelationId,
                step.Chunk?.Activity,
                step.Chunk?.ReplyToken,
                step.Chunk?.ReplyTokenExpiresAtUnixMs ?? 0);

        switch (step.Operation)
        {
            case LarkCardOperationPhase.Create:
                await ExecuteLarkCardCreateOperationAsync(
                    ResolveCardRunner(),
                    step.Chunk?.Clone() ?? new LlmReplyCardStreamChunkEvent(),
                    correlationId,
                    step.StreamingElementId,
                    step.Sequence,
                    step.OperationGeneration,
                    runtimeContext,
                    CancellationToken.None);
                return;
            case LarkCardOperationPhase.Stream:
                await ExecuteLarkCardStreamOperationAsync(
                    ResolveCardRunner(),
                    step.Chunk?.Clone() ?? new LlmReplyCardStreamChunkEvent(),
                    correlationId,
                    step.CardId,
                    step.StreamingElementId,
                    step.Sequence,
                    step.OperationGeneration,
                    runtimeContext,
                    CancellationToken.None);
                return;
            case LarkCardOperationPhase.Finalize:
                RestoreRuntimeTransportCredentials(step.Activity, runtimeContext);
                await ExecuteLarkCardFinalizeOperationAsync(
                    ResolveCardRunner(),
                    step.Activity?.Clone() ?? new ChatActivity(),
                    correlationId,
                    step.CommandId,
                    step.CardId,
                    step.CardMessageId,
                    step.StreamingElementId,
                    step.FinalText,
                    step.LastFlushedText,
                    step.FinalDiffers,
                    step.AppendedHistory.ToArray(),
                    step.Sequence,
                    step.OperationGeneration,
                    runtimeContext,
                    CancellationToken.None);
                return;
        }
    }

    private static LarkCardOperationRawResult ToRawResult(ConversationCardCreateResult result) =>
        new()
        {
            CardId = result.CardId ?? string.Empty,
            CardMessageId = result.CardMessageId ?? string.Empty,
            IsRateLimited = result.IsRateLimited,
            IsTableLimitExceeded = result.IsTableLimitExceeded,
            IsCardUnavailable = result.IsCardUnavailable,
            IsPostSendFailure = result.IsPostSendFailure,
            RawErrorCode = result.ErrorCode ?? string.Empty,
            RawErrorSummary = result.ErrorSummary ?? string.Empty,
        };

    private static LarkCardOperationRawResult ToRawResult(ConversationCardStreamResult result) =>
        new()
        {
            IsRateLimited = result.IsRateLimited,
            IsTableLimitExceeded = result.IsTableLimitExceeded,
            IsCardUnavailable = result.IsCardUnavailable,
            RawErrorCode = result.ErrorCode ?? string.Empty,
            RawErrorSummary = result.ErrorSummary ?? string.Empty,
        };

    private static LarkCardOperationRawResult ToRawResult(ConversationCardFinalizeResult result) =>
        new()
        {
            FinalTextWritten = result.FinalTextWritten,
            RawErrorCode = result.ErrorCode ?? string.Empty,
            RawErrorSummary = result.ErrorSummary ?? string.Empty,
        };

    private static LarkCardOperationRawResult ToRawFault(Exception ex) =>
        new()
        {
            ExceptionType = ex.GetType().Name,
            ExceptionMessage = ex.Message,
        };

    private static bool MatchesLarkCardInFlight(
        LarkCardStreamingState state,
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
            return false;
        if (!string.IsNullOrWhiteSpace(cardId) &&
            !string.Equals(state.CardId, cardId, StringComparison.Ordinal))
            return false;
        return true;
    }

    private Task<LarkCardStreamingState> PersistLarkCardCoalescedStateAsync(
        string correlationId,
        LarkCardStreamingState state,
        string? accumulatedText = null,
        string? finalizeText = null,
        string? finalizeCommandId = null,
        IEnumerable<ConversationHistoryEntry>? appendedHistory = null) =>
        TransitionLarkCardStreamingPhaseAsync(
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

    /// <summary>
    /// Drives one CardKit-mode streaming chunk. Returns true when the card handler owns the
    /// outcome (Idle->Creating[->Streaming], Streaming->Streaming, terminal-drop) and false
    /// only when the caller should fall through to the legacy text-edit path —
    /// CreationFailed phase signals "card path is dead for this turn, route the rest of the
    /// chunks through edit-message streaming."
    /// </summary>
    private async Task<bool> HandleLarkCardStreamingChunkCoreAsync(
        LlmReplyCardStreamChunkEvent evt,
        string correlationId)
    {
        var state = GetOrInitLarkCardStreamingState(correlationId);

        // Already-decided text-edit fallback: let the caller continue down the text-edit path.
        if (state.Phase is LarkCardStreamingPhase.CreationFailed)
        {
            await ClearReplyLifecycleAsync(correlationId, ConversationReplyLifecycleMode.LarkCard, "card_fallback_or_terminal");
            return false;
        }

        if (ShouldSkipLarkCardStreamingForUnavailable(state, LarkCardStreamingGuardSource.AcceptInterimChunk))
            return true;

        if (state.Phase is LarkCardStreamingPhase.Idle)
        {
            var generation = NextLarkCardOperationGeneration(state);
            await TransitionLarkCardStreamingPhaseAsync(
                correlationId,
                state,
                LarkCardStreamingPhase.Creating,
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
                commandId: BuildLlmReplyCommandId(evt.CorrelationId),
                chunk: evt,
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

        // Streaming: interim element-content update. Sequence pre-incremented; on success
        // record the new sequence + last-flushed text so finalize knows whether to write.
        var nextSequence = state.Sequence + 1;
        var streamGeneration = NextLarkCardOperationGeneration(state);
        await TransitionLarkCardStreamingPhaseAsync(
            correlationId,
            state,
            LarkCardStreamingPhase.Streaming,
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
            BuildLlmReplyCommandId(evt.CorrelationId),
            evt,
            activity: null,
            finalText: null,
            lastFlushedText: state.LastFlushedText,
            CancellationToken.None);
        await StartLarkCardStreamOperationAsync(evt, correlationId, state, nextSequence, streamGeneration);
        return true;
    }

    /// <summary>
    /// Drives the card-mode finalize when <see cref="TryCompleteStreamedReplyAsync"/> sees a
    /// live Streaming phase. Persists a <c>ConversationTurnCompletedEvent</c> with
    /// <c>SentActivityId="lark-card-stream:{cardMessageId}"</c> so observers can distinguish
    /// the card path from the legacy <c>nyx-relay-stream:</c> path.
    /// </summary>
    private async Task<bool> TryCompleteCardStreamedReplyAsync(
        LlmReplyReadyEvent evt,
        string correlationId,
        string commandId,
        ChatActivity? referenceActivity,
        ConversationTurnRuntimeContext runtimeContext)
    {
        var state = GetOrInitLarkCardStreamingState(correlationId);
        // Idle: card path was never started for this turn (or already cleaned up); let the
        // legacy edit-message finalize path handle it. CreationFailed: card create rejected
        // pre-send, which already routed the chunks to the text-edit sink, so the text-edit
        // finalize must run too. Both → return false to fall through.
        if (state.Phase is LarkCardStreamingPhase.Idle)
            return false;
        if (state.Phase is LarkCardStreamingPhase.CreationFailed)
        {
            await ClearReplyLifecycleAsync(correlationId, ConversationReplyLifecycleMode.LarkCard, "card_fallback_or_terminal");
            return false;
        }

        // Already-terminal card phase (post-send-failure, mid-stream rate/unavailable, or
        // a previous finalize): persistence already happened at the transition site, so
        // simply consume the ready event without running text-edit finalize. The
        // ProcessedCommandIds guard in HandleLlmReplyReadyAsync also short-circuits late
        // ready events, but returning true here keeps the contract explicit.
        if (state.Phase is LarkCardStreamingPhase.Completed
                       or LarkCardStreamingPhase.Aborted
                       or LarkCardStreamingPhase.Terminated)
        {
            await ClearReplyLifecycleAsync(correlationId, ConversationReplyLifecycleMode.LarkCard, "card_fallback_or_terminal");
            return true;
        }

        if (state.InFlight is not null)
        {
            await PersistLarkCardCoalescedStateAsync(
                correlationId,
                state,
                finalizeText: evt.Outbound?.Text ?? string.Empty,
                finalizeCommandId: commandId,
                appendedHistory: evt.AppendedHistory);
            return true;
        }

        var finalText = evt.Outbound?.Text ?? string.Empty;
        var finalDiffers = !string.IsNullOrWhiteSpace(finalText)
            && !string.Equals(finalText, state.LastFlushedText, StringComparison.Ordinal);

        var nextSequence = state.Sequence + 1;
        var activityForToken = (referenceActivity ?? evt.Activity)?.Clone() ?? new ChatActivity();
        RestoreRuntimeTransportCredentials(activityForToken, runtimeContext);

        var generation = NextLarkCardOperationGeneration(state);
        await TransitionLarkCardStreamingPhaseAsync(
            correlationId,
            state,
            LarkCardStreamingPhase.Streaming,
            fieldUpdate: s => s with
            {
                InFlight = new LarkCardOperationInFlight(LarkCardOperationPhase.Finalize, nextSequence, generation),
                OperationGeneration = generation,
                PendingFinalizeText = finalText,
                PendingFinalizeCommandId = commandId,
            });
        await ScheduleLarkCardOperationTimeoutAsync(
            correlationId,
            LarkCardOperationPhase.Finalize,
            nextSequence,
            generation,
            state.CardId,
            state.CardMessageId,
            commandId,
            chunk: null,
            activity: activityForToken,
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
            evt.AppendedHistory.ToArray(),
            nextSequence,
            generation,
            runtimeContext);
        return true;
    }

    // CardKit executor continuations are dispatched back to this actor with
    // CreateDirect(Id, Id). Without AllowSelfHandling, StaticHandlerAdapter filters
    // the completed signal after Lark already accepted the card create/stream API call.
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
                Logger.LogDebug(
                    "Ignoring Lark card operation signal with unspecified operation. operationId={OperationId}",
                    evt.OperationId);
                return;
        }
    }

    private async Task HandleLarkCardCreateCompletionAsync(LarkCardOperationCompletedEvent evt)
    {
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return;

        var state = GetOrInitLarkCardStreamingState(correlationId);
        if (!MatchesLarkCardInFlight(state, LarkCardOperationPhase.Create, evt.Sequence, evt.OperationGeneration))
            return;

        var result = ToCreateResult(evt);
        if (!result.Success)
        {
            if (result.IsPostSendFailure)
            {
                Logger.LogWarning(
                    "Card post-send failure; terminating turn without text-edit fallback. correlation={CorrelationId}, code={ErrorCode}, cardId={CardId}",
                    evt.CorrelationId,
                    result.ErrorCode,
                    result.CardId);
                var terminated = await TransitionLarkCardStreamingPhaseAsync(
                    correlationId,
                    state,
                    LarkCardStreamingPhase.Terminated,
                    terminalReason: $"create_post_send_failed:{result.ErrorCode}",
                    fieldUpdate: s => s with
                    {
                        CardId = NormalizeOptional(result.CardId),
                        CardMessageId = NormalizeOptional(result.CardMessageId),
                        OriginalCardId = NormalizeOptional(result.CardId),
                        InFlight = null,
                    });
                await PersistCardStreamedDeliveredCompletionAsync(
                    correlationId,
                    BuildLlmReplyCommandId(evt.Chunk?.CorrelationId ?? correlationId),
                    evt.Chunk?.Activity,
                    terminated.CardMessageId ?? string.Empty,
                    terminated.LastFlushedText,
                    appendedHistory: []);
                return;
            }

            Logger.LogInformation(
                "Card create failed; falling back to text-edit for the rest of this turn. correlation={CorrelationId}, code={ErrorCode}, rateLimited={RateLimited}, tableLimit={TableLimit}, cardUnavailable={CardUnavailable}",
                evt.CorrelationId,
                result.ErrorCode,
                result.IsRateLimited,
                result.IsTableLimitExceeded,
                result.IsCardUnavailable);
            await TransitionLarkCardStreamingPhaseAsync(
                correlationId,
                state,
                LarkCardStreamingPhase.CreationFailed,
                terminalReason: $"create_failed:{result.ErrorCode}",
                fieldUpdate: s => s with { InFlight = null });
            if (evt.Chunk is not null)
                await HandleNyxRelayStreamingChunkCoreAsync(ToTextStreamChunk(evt.Chunk));
            return;
        }

        var accumulatedText = state.PendingAccumulatedText ?? evt.Chunk?.AccumulatedText ?? string.Empty;
        var streaming = await TransitionLarkCardStreamingPhaseAsync(
            correlationId,
            state,
            LarkCardStreamingPhase.Streaming,
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

        var state = GetOrInitLarkCardStreamingState(correlationId);
        if (!MatchesLarkCardInFlight(
                state,
                LarkCardOperationPhase.Stream,
                evt.Sequence,
                evt.OperationGeneration,
                evt.CardId))
            return;

        var result = ToStreamResult(evt);
        if (!result.Success)
        {
            if (result.IsRateLimited)
            {
                Logger.LogDebug(
                    "Card stream rate-limited; dropping frame. correlation={CorrelationId}, seq={Sequence}",
                    evt.CorrelationId,
                    evt.Sequence);
                var recovered = await TransitionLarkCardStreamingPhaseAsync(
                    correlationId,
                    state,
                    LarkCardStreamingPhase.Streaming,
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
                Logger.LogWarning(
                    "Card stream terminal failure; ending turn. correlation={CorrelationId}, code={ErrorCode}",
                    evt.CorrelationId,
                    result.ErrorCode);
                var terminated = await TransitionLarkCardStreamingPhaseAsync(
                    correlationId,
                    state,
                    LarkCardStreamingPhase.Terminated,
                    terminalReason: $"stream_failed:{result.ErrorCode}",
                    fieldUpdate: s => s with { InFlight = null });
                await PersistCardStreamedDeliveredCompletionAsync(
                    correlationId,
                    BuildLlmReplyCommandId(evt.Chunk?.CorrelationId ?? correlationId),
                    evt.Chunk?.Activity,
                    terminated.CardMessageId ?? string.Empty,
                    terminated.LastFlushedText,
                    appendedHistory: []);
                return;
            }

            Logger.LogInformation(
                "Card stream non-terminal failure; continuing. correlation={CorrelationId}, code={ErrorCode}",
                evt.CorrelationId,
                result.ErrorCode);
            var continued = await TransitionLarkCardStreamingPhaseAsync(
                correlationId,
                state,
                LarkCardStreamingPhase.Streaming,
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
        var updated = await TransitionLarkCardStreamingPhaseAsync(
            correlationId,
            state,
            LarkCardStreamingPhase.Streaming,
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

        var state = GetOrInitLarkCardStreamingState(correlationId);
        if (!MatchesLarkCardInFlight(
                state,
                LarkCardOperationPhase.Finalize,
                evt.Sequence,
                evt.OperationGeneration,
                evt.CardId))
            return;

        var result = ToFinalizeResult(evt);
        var finalText = state.PendingFinalizeText ?? evt.FinalText ?? string.Empty;
        var commandId = state.PendingFinalizeCommandId ?? evt.CommandId ?? BuildLlmReplyCommandId(correlationId);
        var visibleText = result.FinalTextWritten ? finalText : state.LastFlushedText;
        var cardMessageId = state.CardMessageId ?? evt.CardMessageId ?? string.Empty;
        if (result.Success)
        {
            await TransitionLarkCardStreamingPhaseAsync(
                correlationId,
                state,
                LarkCardStreamingPhase.Completed,
                terminalReason: "completed",
                fieldUpdate: s => s with { InFlight = null });
            await PersistCardStreamedDeliveredCompletionAsync(
                correlationId,
                commandId,
                evt.Activity,
                cardMessageId,
                visibleText,
                evt.AppendedHistory.ToArray());
            return;
        }

        Logger.LogWarning(
            "Card finalize failed; persisting partial. correlation={CorrelationId}, code={ErrorCode}",
            evt.CorrelationId,
            result.ErrorCode);
        await TransitionLarkCardStreamingPhaseAsync(
            correlationId,
            state,
            LarkCardStreamingPhase.Terminated,
            terminalReason: $"finalize_failed:{result.ErrorCode}",
            fieldUpdate: s => s with { InFlight = null });
        await PersistCardStreamedFailedCompletionAsync(
            correlationId,
            commandId,
            evt.Activity,
            cardMessageId,
            visibleText,
            result.ErrorCode,
            result.ErrorSummary,
            evt.AppendedHistory.ToArray());
    }

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

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleLarkCardOperationTimeoutFiredAsync(LarkCardOperationTimeoutFiredEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return;

        var state = GetOrInitLarkCardStreamingState(correlationId);
        if (!MatchesLarkCardInFlight(state, evt.Operation, evt.Sequence, evt.OperationGeneration, evt.CardId))
            return;

        switch (evt.Operation)
        {
            case LarkCardOperationPhase.Create:
                await TransitionLarkCardStreamingPhaseAsync(
                    correlationId,
                    state,
                    LarkCardStreamingPhase.CreationFailed,
                    terminalReason: "create_timeout",
                    fieldUpdate: s => s with { InFlight = null });
                return;
            case LarkCardOperationPhase.Stream:
            {
                var recovered = await TransitionLarkCardStreamingPhaseAsync(
                    correlationId,
                    state,
                    LarkCardStreamingPhase.Streaming,
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
                await TransitionLarkCardStreamingPhaseAsync(
                    correlationId,
                    state,
                    LarkCardStreamingPhase.Terminated,
                    terminalReason: "finalize_timeout",
                    fieldUpdate: s => s with { InFlight = null });
                await PersistCardStreamedFailedCompletionAsync(
                    correlationId,
                    NormalizeOptional(evt.CommandId) ?? state.PendingFinalizeCommandId ?? BuildLlmReplyCommandId(correlationId),
                    evt.Activity,
                    state.CardMessageId ?? evt.CardMessageId ?? string.Empty,
                    state.LastFlushedText,
                    "finalize_timeout",
                    "Card finalize operation timed out.",
                    appendedHistory: []);
                return;
        }
    }

    private static LlmReplyStreamChunkEvent ToTextStreamChunk(LlmReplyCardStreamChunkEvent evt) =>
        new()
        {
            CorrelationId = evt.CorrelationId,
            RegistrationId = evt.RegistrationId,
            Activity = evt.Activity?.Clone(),
            AccumulatedText = evt.AccumulatedText,
            ChunkAtUnixMs = evt.ChunkAtUnixMs,
            ReplyToken = evt.ReplyToken,
            ReplyTokenExpiresAtUnixMs = evt.ReplyTokenExpiresAtUnixMs,
        };

    private async Task ContinueLarkCardCoalescedWorkAsync(
        string correlationId,
        LarkCardStreamingState state,
        LlmReplyCardStreamChunkEvent? sourceChunk)
    {
        if (state.Phase is not LarkCardStreamingPhase.Streaming || state.InFlight is not null)
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
            await TransitionLarkCardStreamingPhaseAsync(
                correlationId,
                state,
                LarkCardStreamingPhase.Streaming,
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
                chunk: null,
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
                generation,
                runtimeContext);
            return;
        }

        if (state.PendingAccumulatedText is null || sourceChunk is null)
            return;

        var nextChunk = sourceChunk.Clone();
        nextChunk.AccumulatedText = state.PendingAccumulatedText;
        var streamSequence = state.Sequence + 1;
        var streamGeneration = NextLarkCardOperationGeneration(state);
        await TransitionLarkCardStreamingPhaseAsync(
            correlationId,
            state,
            LarkCardStreamingPhase.Streaming,
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
            BuildLlmReplyCommandId(nextChunk.CorrelationId),
            nextChunk,
            activity: null,
            finalText: null,
            lastFlushedText: state.LastFlushedText,
            CancellationToken.None);
        await StartLarkCardStreamOperationAsync(nextChunk, correlationId, state, streamSequence, streamGeneration);
    }

    /// <summary>
    /// Persists the terminal <c>ConversationTurnCompletedEvent</c> for a card-streamed turn.
    /// Decoupled from the inbound event type so both the LlmReplyReady finalize path and the
    /// mid-stream Terminated path (post-send-failure / table-limit / unavailable, observed
    /// while still processing chunks) can share one writer.
    /// </summary>
    // Refactor (iter20/cluster-004):
    //   Old pattern: Card completion removed process-local token/card registries after writing only completion.
    //   New principle: Persist delivered + completion facts and clear the actor-owned lifecycle state explicitly.
    private Task PersistCardStreamedDeliveredCompletionAsync(
        string correlationId,
        string commandId,
        ChatActivity? eventActivity,
        string cardMessageId,
        string outboundText,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory) =>
        PersistCardStreamedCompletionAsync(
            correlationId,
            commandId,
            eventActivity,
            cardMessageId,
            outboundText,
            deliveryFailure: null,
            appendedHistory);

    private Task PersistCardStreamedFailedCompletionAsync(
        string correlationId,
        string commandId,
        ChatActivity? eventActivity,
        string cardMessageId,
        string outboundText,
        string errorCode,
        string errorSummary,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory) =>
        PersistCardStreamedCompletionAsync(
            correlationId,
            commandId,
            eventActivity,
            cardMessageId,
            outboundText,
            new LlmReplyDeliveryFailedEvent
            {
                CorrelationId = correlationId,
                RunId = ResolvePendingLlmReplyRunId(correlationId) ?? string.Empty,
                FailedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ErrorCode = errorCode ?? string.Empty,
                ErrorMessage = errorSummary ?? string.Empty,
            },
            appendedHistory);

    private async Task PersistCardStreamedCompletionAsync(
        string correlationId,
        string commandId,
        ChatActivity? eventActivity,
        string cardMessageId,
        string outboundText,
        LlmReplyDeliveryFailedEvent? deliveryFailure,
        IReadOnlyList<ConversationHistoryEntry> appendedHistory)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var completed = new ConversationTurnCompletedEvent
        {
            ProcessedActivityId = string.Empty,
            CausationCommandId = commandId,
            SentActivityId = $"lark-card-stream:{cardMessageId}",
            AuthPrincipal = "bot",
            Conversation = eventActivity?.Conversation?.Clone()
                           ?? State.Conversation?.Clone()
                           ?? new ConversationReference(),
            Outbound = new MessageContent { Text = outboundText },
            CompletedAtUnixMs = nowMs,
            OutboundDelivery = ToOutboundDeliveryReceipt(eventActivity?.OutboundDelivery),
        };
        completed.AppendedHistory.AddRange(appendedHistory.Select(entry => entry.Clone()));
        if (deliveryFailure is null)
        {
            var delivered = new LlmReplyDeliveredEvent
            {
                CorrelationId = correlationId,
                RunId = ResolvePendingLlmReplyRunId(correlationId) ?? string.Empty,
                AckedAtUnixMs = nowMs,
                ChannelMessageId = $"lark-card-stream:{cardMessageId}",
            };
            await PersistDomainEventAsync(delivered);
            if (eventActivity is not null)
                _ = ResolveRunner().OnReplyDeliveredAsync(eventActivity, CancellationToken.None);
        }
        else
        {
            if (deliveryFailure.FailedAtUnixMs <= 0)
                deliveryFailure.FailedAtUnixMs = nowMs;
            await PersistDomainEventAsync(deliveryFailure);
        }

        await ClearReplyLifecycleAsync(correlationId, ConversationReplyLifecycleMode.LarkCard, "card_fallback_or_terminal");
        await PersistDomainEventAsync(completed);
        Logger.LogInformation(
            "Completed card-streamed LLM reply: correlation={CorrelationId} cardMessageId={CardMessageId} conversation={Key}",
            correlationId,
            cardMessageId,
            completed.Conversation?.CanonicalKey);
    }
}
