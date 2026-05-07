using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ConversationGAgent
{
    private readonly Dictionary<string, LarkCardStreamingState> _larkCardStreamingStates = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-turn phase of the Lark CardKit streaming pipeline. Distinct from
    /// <see cref="NyxRelayStreamingPhase"/> (which models channel-relay edit-message
    /// streaming): card streaming has its own lifecycle (allocate card entity, bind to
    /// chat, stream element content, close streaming mode) and goes through the API-key
    /// proxy directly rather than channel-relay's <c>/reply{,/update}</c> surface.
    /// </summary>
    /// <remarks>
    /// Fallback semantics: when card creation fails (<see cref="CreationFailed"/>), the
    /// dispatcher routes the turn to the legacy text-edit sink (<c>NyxRelayStreamingPhase</c>
    /// machine). Once <see cref="Streaming"/> is reached, the card path owns the turn —
    /// mid-stream rate-limit / table-limit failures terminate the turn at
    /// <see cref="Terminated"/> with the last flushed text persisted as partial.
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

    /// <summary>
    /// Actor-scoped, in-memory streaming state for one CardKit-driven turn. Keyed by
    /// <c>correlation_id</c>, same lifecycle as <see cref="NyxRelayReplyTokenContext"/>.
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
        string? TerminalReason)
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
            TerminalReason: null);

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

            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.Streaming) => true,
            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.CreationFailed) => true,
            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.Terminated) => true,

            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Streaming) => true,
            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Completed) => true,
            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Aborted) => true,
            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Terminated) => true,

            _ => false,
        };

    private LarkCardStreamingState GetOrInitLarkCardStreamingState(string correlationId) =>
        _larkCardStreamingStates.GetValueOrDefault(correlationId) ?? LarkCardStreamingState.Initial;

    private static bool ShouldSkipLarkCardStreamingForUnavailable(
        LarkCardStreamingState state,
        LarkCardStreamingGuardSource source) =>
        source switch
        {
            LarkCardStreamingGuardSource.AcceptInterimChunk => !state.AllowsInterimEdit,
            LarkCardStreamingGuardSource.Finalize => !state.AllowsFinalize,
            _ => false,
        };

    private LarkCardStreamingState TransitionLarkCardStreamingPhase(
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
            TerminalReason = IsTerminalLarkCardStreamingPhase(next)
                ? (terminalReason ?? carried.TerminalReason)
                : carried.TerminalReason,
        };
        _larkCardStreamingStates[correlationId] = updated;
        return updated;
    }
}
