using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ConversationGAgent
{
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

    /// <summary>
    /// Actor-scoped, in-memory streaming state for one conversation turn. Never persisted.
    /// Keyed by <c>correlation_id</c>, same lifecycle as <see cref="NyxRelayReplyTokenContext"/>.
    /// </summary>
    private sealed record NyxRelayStreamingState(
        NyxRelayStreamingPhase Phase,
        string? PlatformMessageId,
        string LastFlushedText,
        int EditCount,
        string? TerminalReason)
    {
        public static NyxRelayStreamingState Initial { get; } =
            new(NyxRelayStreamingPhase.Idle, null, string.Empty, 0, null);

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
            (NyxRelayStreamingPhase.Idle, NyxRelayStreamingPhase.PlaceholderSent) => true,
            (NyxRelayStreamingPhase.Idle, NyxRelayStreamingPhase.DisabledPreSend) => true,

            (NyxRelayStreamingPhase.PlaceholderSent, NyxRelayStreamingPhase.Streaming) => true,
            (NyxRelayStreamingPhase.PlaceholderSent, NyxRelayStreamingPhase.SuppressingInterim) => true,
            (NyxRelayStreamingPhase.PlaceholderSent, NyxRelayStreamingPhase.TerminalSucceeded) => true,
            (NyxRelayStreamingPhase.PlaceholderSent, NyxRelayStreamingPhase.TerminalPartial) => true,

            (NyxRelayStreamingPhase.Streaming, NyxRelayStreamingPhase.Streaming) => true,
            (NyxRelayStreamingPhase.Streaming, NyxRelayStreamingPhase.SuppressingInterim) => true,
            (NyxRelayStreamingPhase.Streaming, NyxRelayStreamingPhase.TerminalSucceeded) => true,
            (NyxRelayStreamingPhase.Streaming, NyxRelayStreamingPhase.TerminalPartial) => true,

            (NyxRelayStreamingPhase.SuppressingInterim, NyxRelayStreamingPhase.TerminalSucceeded) => true,
            (NyxRelayStreamingPhase.SuppressingInterim, NyxRelayStreamingPhase.TerminalPartial) => true,

            _ => false,
        };

    private NyxRelayStreamingState GetOrInitNyxRelayStreamingState(string correlationId) =>
        _nyxRelayStreamingStates.GetValueOrDefault(correlationId) ?? NyxRelayStreamingState.Initial;

    /// <summary>
    /// Single guard that owns the "should this streaming callback short-circuit?" decision.
    /// Every public handler that touches the streaming path defers to this helper at the
    /// top instead of repeating ad-hoc checks. Returns true when the caller should bail.
    /// </summary>
    private static bool ShouldSkipNyxRelayStreamingForUnavailable(
        NyxRelayStreamingState state,
        NyxRelayStreamingGuardSource source) =>
        source switch
        {
            NyxRelayStreamingGuardSource.AcceptInterimChunk => !state.AllowsInterimEdit,
            NyxRelayStreamingGuardSource.Finalize => state.AllowsReplyFallback,
            _ => false,
        };

    /// <summary>
    /// Validates the transition, applies <paramref name="fieldUpdate"/> if any, writes the
    /// updated state, and returns it. Illegal transitions are logged at warn level and
    /// return the unchanged current state — actor turns must keep making progress.
    /// </summary>
    private NyxRelayStreamingState TransitionNyxRelayStreamingPhase(
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
            TerminalReason = IsTerminalNyxRelayStreamingPhase(next)
                ? (terminalReason ?? carried.TerminalReason)
                : carried.TerminalReason,
        };
        _nyxRelayStreamingStates[correlationId] = updated;
        return updated;
    }
}
