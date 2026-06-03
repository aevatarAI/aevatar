using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ConversationGAgent
{
    // Refactor (iter20/cluster-004):
    //   Old pattern: ConversationGAgent 持有 actor token registry + 可见回复状态部分仅在内存
    //   New principle: 删 actor token registry,credentials runtime-only,可见回复 lifecycle 持久到 ConversationGAgent state
    /// <summary>
    /// Per-turn phase of the NyxID-relay edit-message streaming pipeline.
    /// </summary>
    /// <remarks>
    /// The reply token consumes on the first successful send. After that, only
    /// <c>/reply/update</c> is valid; falling back to <c>/reply</c> would reuse a dead JTI
    /// and surface as 401. The two boolean flags this enum replaces (<c>Disabled</c> +
    /// <c>SuppressInterim</c>) failed to express that asymmetry directly, so callers had
    /// to derive it from <c>PlatformMessageId</c> emptiness. The phase enum makes the
    /// asymmetry the primary state.
    /// </remarks>
    private enum NyxRelayStreamingPhase
    {
        Idle,
        PlaceholderSent,
        Streaming,
        SuppressingInterim,
        DisabledPreSend,
        TerminalSucceeded,
        TerminalPartial,
    }

    /// <summary>
    /// Identifies which streaming entry point is asking the unavailable guard to decide
    /// whether to short-circuit. Different sources have different "should I bail?" semantics.
    /// </summary>
    private enum NyxRelayStreamingGuardSource
    {
        AcceptInterimChunk,
        Finalize,
    }

    private sealed record NyxRelayTextOperationInFlight(
        NyxRelayTextOperationKind Operation,
        long Sequence,
        long Generation);

    /// <summary>
    /// Actor-scoped streaming state for one conversation turn, backed by
    /// ConversationGAgentState.ActiveReplyLifecycles.
    /// </summary>
    private sealed record NyxRelayStreamingState(
        NyxRelayStreamingPhase Phase,
        string? PlatformMessageId,
        string LastFlushedText,
        int EditCount,
        string? TerminalReason,
        NyxRelayTextOperationInFlight? InFlight,
        long OperationGeneration,
        string? PendingAccumulatedText,
        string? PendingFinalizeText,
        string? PendingFinalizeCommandId,
        LlmReplyTerminalState PendingTerminalState,
        IReadOnlyList<ConversationHistoryEntry> PendingAppendedHistory,
        int RetryAttempt)
    {
        public static NyxRelayStreamingState Initial { get; } =
            new(
                NyxRelayStreamingPhase.Idle,
                PlatformMessageId: null,
                LastFlushedText: string.Empty,
                EditCount: 0,
                TerminalReason: null,
                InFlight: null,
                OperationGeneration: 0,
                PendingAccumulatedText: null,
                PendingFinalizeText: null,
                PendingFinalizeCommandId: null,
                PendingTerminalState: LlmReplyTerminalState.Unspecified,
                PendingAppendedHistory: [],
                RetryAttempt: 0);

        public bool AllowsInterimEdit =>
            Phase is NyxRelayStreamingPhase.Idle
                  or NyxRelayStreamingPhase.PlaceholderSent
                  or NyxRelayStreamingPhase.Streaming;

        public bool AllowsFinalEdit =>
            Phase is NyxRelayStreamingPhase.PlaceholderSent
                  or NyxRelayStreamingPhase.Streaming
                  or NyxRelayStreamingPhase.SuppressingInterim;

        public bool AllowsReplyFallback =>
            Phase is NyxRelayStreamingPhase.Idle
                  or NyxRelayStreamingPhase.DisabledPreSend;
    }

    private static bool IsTerminalNyxRelayStreamingPhase(NyxRelayStreamingPhase phase) =>
        phase is NyxRelayStreamingPhase.DisabledPreSend
              or NyxRelayStreamingPhase.TerminalSucceeded
              or NyxRelayStreamingPhase.TerminalPartial;

    private static bool IsLegalNyxRelayStreamingTransition(NyxRelayStreamingPhase from, NyxRelayStreamingPhase to) =>
        (from, to) switch
        {
            (NyxRelayStreamingPhase.Idle, NyxRelayStreamingPhase.Idle) => true,
            (NyxRelayStreamingPhase.Idle, NyxRelayStreamingPhase.PlaceholderSent) => true,
            (NyxRelayStreamingPhase.Idle, NyxRelayStreamingPhase.DisabledPreSend) => true,

            (NyxRelayStreamingPhase.PlaceholderSent, NyxRelayStreamingPhase.PlaceholderSent) => true,
            (NyxRelayStreamingPhase.PlaceholderSent, NyxRelayStreamingPhase.Streaming) => true,
            (NyxRelayStreamingPhase.PlaceholderSent, NyxRelayStreamingPhase.SuppressingInterim) => true,
            (NyxRelayStreamingPhase.PlaceholderSent, NyxRelayStreamingPhase.TerminalSucceeded) => true,
            (NyxRelayStreamingPhase.PlaceholderSent, NyxRelayStreamingPhase.TerminalPartial) => true,

            (NyxRelayStreamingPhase.Streaming, NyxRelayStreamingPhase.Streaming) => true,
            (NyxRelayStreamingPhase.Streaming, NyxRelayStreamingPhase.SuppressingInterim) => true,
            (NyxRelayStreamingPhase.Streaming, NyxRelayStreamingPhase.TerminalSucceeded) => true,
            (NyxRelayStreamingPhase.Streaming, NyxRelayStreamingPhase.TerminalPartial) => true,

            (NyxRelayStreamingPhase.SuppressingInterim, NyxRelayStreamingPhase.SuppressingInterim) => true,
            (NyxRelayStreamingPhase.SuppressingInterim, NyxRelayStreamingPhase.TerminalSucceeded) => true,
            (NyxRelayStreamingPhase.SuppressingInterim, NyxRelayStreamingPhase.TerminalPartial) => true,

            _ => false,
        };

    // Refactor (iter20/cluster-004):
    //   Old pattern: Nyx relay streaming lifecycle lived in an actor token registry / process-memory dictionary.
    //   New principle: Rehydrate visible reply lifecycle from actor-owned persisted state; keep credentials runtime-only.
    private NyxRelayStreamingState GetOrInitNyxRelayStreamingState(string correlationId)
    {
        var lifecycle = FindReplyLifecycle(correlationId, ConversationReplyLifecycleMode.NyxRelayText);
        if (lifecycle is null)
            return NyxRelayStreamingState.Initial;

        return new NyxRelayStreamingState(
            ToNyxRelayStreamingPhase(lifecycle.Phase),
            NormalizeOptional(lifecycle.PlatformMessageId),
            lifecycle.LastFlushedText ?? string.Empty,
            lifecycle.EditCount,
            NormalizeOptional(lifecycle.TerminalReason),
            lifecycle.NyxRelayInFlightOperation == NyxRelayTextOperationKind.Unspecified
                ? null
                : new NyxRelayTextOperationInFlight(
                    lifecycle.NyxRelayInFlightOperation,
                    lifecycle.NyxRelayInFlightSequence,
                    lifecycle.NyxRelayOperationGeneration),
            lifecycle.NyxRelayOperationGeneration,
            NormalizeOptional(lifecycle.PendingAccumulatedText),
            NormalizeOptional(lifecycle.PendingFinalizeText),
            NormalizeOptional(lifecycle.PendingFinalizeCommandId),
            lifecycle.PendingNyxRelayTerminalState,
            lifecycle.PendingAppendedHistory.Select(entry => entry.Clone()).ToArray(),
            lifecycle.NyxRelayRetryAttempt);
    }

    /// <summary>
    /// Single guard that owns the "should this streaming callback short-circuit?" decision.
    /// Every public handler that touches the streaming path defers to this helper at the
    /// top instead of repeating ad-hoc checks. Returns true when the caller should bail.
    /// </summary>
    /// <remarks>
    /// The Finalize branch also short-circuits when <see cref="NyxRelayStreamingState.PlatformMessageId"/>
    /// is empty: a turn whose first send did not surface a platform message id (Nyx returned
    /// an empty <c>PlatformMessageId</c> on initial <c>/reply</c>) cannot be finalized via
    /// <c>/reply/update</c> — we have no upstream message to address — so the legacy
    /// <c>RunLlmReplyAsync</c> fallback owns the terminal user-visible state. This preserves
    /// the explicit empty-PlatformMessageId check that lived in the pre-refactor path.
    /// </remarks>
    private static bool ShouldSkipNyxRelayStreamingForUnavailable(
        NyxRelayStreamingState state,
        NyxRelayStreamingGuardSource source) =>
        source switch
        {
            NyxRelayStreamingGuardSource.AcceptInterimChunk => !state.AllowsInterimEdit,
            NyxRelayStreamingGuardSource.Finalize =>
                state.AllowsReplyFallback || string.IsNullOrEmpty(state.PlatformMessageId),
            _ => false,
        };

    /// <summary>
    /// Validates the transition, applies <paramref name="fieldUpdate"/> if any, writes the
    /// updated state, and returns it. Illegal transitions are logged at warn level and
    /// return the unchanged current state — actor turns must keep making progress.
    /// </summary>
    // Refactor (iter80/cluster-081-channel-reply-lifecycle-event-state-schema):
    //   Old pattern: ConversationReplyLifecycleChangedEvent carried full ConversationReplyLifecycleState
    //   New principle: event describes transition facts; reducer derives current state from event + actor state
    private async Task<NyxRelayStreamingState> TransitionNyxRelayStreamingPhaseAsync(
        string correlationId,
        NyxRelayStreamingState current,
        NyxRelayStreamingPhase next,
        string? terminalReason = null,
        Func<NyxRelayStreamingState, NyxRelayStreamingState>? fieldUpdate = null)
    {
        if (!IsLegalNyxRelayStreamingTransition(current.Phase, next))
        {
            Logger.LogWarning(
                "Illegal Nyx relay streaming phase transition {From}->{To} for correlation={CorrelationId}; keeping current state",
                current.Phase, next, correlationId);
            return current;
        }

        var carried = fieldUpdate?.Invoke(current) ?? current;
        var updated = carried with
        {
            Phase = next,
            InFlight = IsTerminalNyxRelayStreamingPhase(next) ? null : carried.InFlight,
            PendingAccumulatedText = IsTerminalNyxRelayStreamingPhase(next) ? null : carried.PendingAccumulatedText,
            PendingFinalizeText = IsTerminalNyxRelayStreamingPhase(next) ? null : carried.PendingFinalizeText,
            PendingFinalizeCommandId = IsTerminalNyxRelayStreamingPhase(next) ? null : carried.PendingFinalizeCommandId,
            PendingAppendedHistory = IsTerminalNyxRelayStreamingPhase(next)
                ? []
                : carried.PendingAppendedHistory,
            PendingTerminalState = IsTerminalNyxRelayStreamingPhase(next)
                ? LlmReplyTerminalState.Unspecified
                : carried.PendingTerminalState,
            RetryAttempt = IsTerminalNyxRelayStreamingPhase(next)
                ? 0
                : carried.RetryAttempt,
            TerminalReason = IsTerminalNyxRelayStreamingPhase(next)
                ? (terminalReason ?? carried.TerminalReason)
                : carried.TerminalReason,
        };
        var changedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await PersistDomainEventAsync(ToLifecycleChangedEvent(correlationId, current, updated, changedAtUnixMs));
        return updated;
    }

    private ConversationReplyLifecycleState? FindReplyLifecycle(
        string? correlationId,
        ConversationReplyLifecycleMode mode)
    {
        var normalized = NormalizeOptional(correlationId);
        if (normalized is null)
            return null;

        return State.ActiveReplyLifecycles.FirstOrDefault(lifecycle =>
            lifecycle.Mode == mode &&
            string.Equals(lifecycle.CorrelationId, normalized, StringComparison.Ordinal));
    }

    private static NyxRelayStreamingPhase ToNyxRelayStreamingPhase(ConversationReplyLifecyclePhase phase) =>
        phase switch
        {
            ConversationReplyLifecyclePhase.TextPlaceholderSent => NyxRelayStreamingPhase.PlaceholderSent,
            ConversationReplyLifecyclePhase.TextStreaming => NyxRelayStreamingPhase.Streaming,
            ConversationReplyLifecyclePhase.TextSuppressingInterim => NyxRelayStreamingPhase.SuppressingInterim,
            ConversationReplyLifecyclePhase.TextDisabledPreSend => NyxRelayStreamingPhase.DisabledPreSend,
            ConversationReplyLifecyclePhase.TextTerminalSucceeded => NyxRelayStreamingPhase.TerminalSucceeded,
            ConversationReplyLifecyclePhase.TextTerminalPartial => NyxRelayStreamingPhase.TerminalPartial,
            _ => NyxRelayStreamingPhase.Idle,
        };

    private static ConversationReplyLifecyclePhase ToLifecyclePhase(NyxRelayStreamingPhase phase) =>
        phase switch
        {
            NyxRelayStreamingPhase.PlaceholderSent => ConversationReplyLifecyclePhase.TextPlaceholderSent,
            NyxRelayStreamingPhase.Streaming => ConversationReplyLifecyclePhase.TextStreaming,
            NyxRelayStreamingPhase.SuppressingInterim => ConversationReplyLifecyclePhase.TextSuppressingInterim,
            NyxRelayStreamingPhase.DisabledPreSend => ConversationReplyLifecyclePhase.TextDisabledPreSend,
            NyxRelayStreamingPhase.TerminalSucceeded => ConversationReplyLifecyclePhase.TextTerminalSucceeded,
            NyxRelayStreamingPhase.TerminalPartial => ConversationReplyLifecyclePhase.TextTerminalPartial,
            _ => ConversationReplyLifecyclePhase.TextIdle,
        };

    private static ConversationReplyLifecycleChangedEvent ToLifecycleChangedEvent(
        string correlationId,
        NyxRelayStreamingState current,
        NyxRelayStreamingState updated,
        long changedAtUnixMs)
    {
        var evt = new ConversationReplyLifecycleChangedEvent
        {
            CorrelationId = correlationId,
            Mode = ConversationReplyLifecycleMode.NyxRelayText,
            PreviousPhase = ToLifecyclePhase(current.Phase),
            Phase = ToLifecyclePhase(updated.Phase),
            ChangedAtUnixMs = changedAtUnixMs,
        };

        if (!string.Equals(current.PlatformMessageId, updated.PlatformMessageId, StringComparison.Ordinal))
            evt.PlatformMessageIdAssigned = updated.PlatformMessageId ?? string.Empty;
        if (!string.Equals(current.LastFlushedText, updated.LastFlushedText, StringComparison.Ordinal))
            evt.FlushedTextDelta = updated.LastFlushedText ?? string.Empty;
        if (current.EditCount != updated.EditCount)
            evt.EditCountDelta = updated.EditCount - current.EditCount;
        if (!string.Equals(current.TerminalReason, updated.TerminalReason, StringComparison.Ordinal))
            evt.TerminalReason = updated.TerminalReason ?? string.Empty;

        var currentOperation = current.InFlight?.Operation ?? NyxRelayTextOperationKind.Unspecified;
        var updatedOperation = updated.InFlight?.Operation ?? NyxRelayTextOperationKind.Unspecified;
        if (currentOperation != updatedOperation)
            evt.NyxRelayOperation = updatedOperation;

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
        if (current.PendingTerminalState != updated.PendingTerminalState)
            evt.NyxRelayTerminalState = updated.PendingTerminalState;
        if (!HistoryEntriesEqual(current.PendingAppendedHistory, updated.PendingAppendedHistory))
            evt.AppendedHistory.AddRange(updated.PendingAppendedHistory.Select(entry => entry.Clone()));
        if (current.RetryAttempt != updated.RetryAttempt)
            evt.NyxRelayRetryAttempt = updated.RetryAttempt;

        return evt;
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

    private long NextNyxRelayTextOperationGeneration(NyxRelayStreamingState state) =>
        Math.Max(state.OperationGeneration, state.InFlight?.Generation ?? 0) + 1;

    private static string BuildNyxRelayTextOperationTimeoutCallbackId(
        string correlationId,
        NyxRelayTextOperationKind operation,
        long generation) =>
        $"conversation-nyx-relay-text:{correlationId}:{operation}:{generation}";

    private static string BuildNyxRelayTextOperationId(
        string correlationId,
        NyxRelayTextOperationKind operation,
        long sequence,
        long generation) =>
        $"{correlationId}:{operation}:{sequence}:{generation}";

    private static bool MatchesNyxRelayTextInFlight(
        NyxRelayStreamingState state,
        NyxRelayTextOperationKind operation,
        long sequence,
        long generation)
    {
        if (state.InFlight is not { } inFlight)
            return false;
        return inFlight.Operation == operation &&
               inFlight.Sequence == sequence &&
               inFlight.Generation == generation;
    }
}
