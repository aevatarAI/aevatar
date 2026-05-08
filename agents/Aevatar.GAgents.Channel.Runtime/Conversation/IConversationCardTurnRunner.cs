using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Runs the CardKit-streaming variant of a bot turn inside <see cref="ConversationGAgent"/>.
/// Parallel to <see cref="IConversationTurnRunner"/> but with three distinct operations
/// (create-and-send, interim element stream, finalize) to match Lark CardKit's lifecycle.
/// The grain owns the per-turn <c>LarkCardStreamingState</c>; this seam only does the
/// outbound call and translates the response into a runner-shaped result.
/// </summary>
/// <remarks>
/// All three operations are invoked under the actor's turn-serial invariant, so the runner
/// implementation must be safe under that single-threaded contract. The
/// <c>sequence</c> parameter is owned by the grain (pre-incremented before each call) and
/// passed verbatim into the CardKit API.
/// </remarks>
public interface IConversationCardTurnRunner
{
    /// <summary>
    /// Allocates a new CardKit card entity (<c>POST /open-apis/cardkit/v1/cards</c>), binds it
    /// to the chat via an interactive <c>im/v1/messages</c> send referencing the new
    /// <c>card_id</c>, and writes the initial accumulated text into
    /// <paramref name="streamingElementId"/>. Implicit sequence = 1.
    /// </summary>
    Task<ConversationCardCreateResult> RunCardCreateAsync(
        LlmReplyCardStreamChunkEvent chunk,
        string streamingElementId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct);

    /// <summary>
    /// Streams the latest accumulated text into the existing card element. Sequence is
    /// pre-incremented by the grain. Lark rejects stale sequences deterministically.
    /// </summary>
    Task<ConversationCardStreamResult> RunCardStreamAsync(
        LlmReplyCardStreamChunkEvent chunk,
        string cardId,
        string elementId,
        long sequence,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct);

    /// <summary>
    /// Closes the card's streaming mode (cursor disappears) and, if the final text differs
    /// from the last interim flush, writes one more element-content update so the persisted
    /// card matches the LLM's final output.
    /// </summary>
    /// <param name="referenceActivity">
    /// Carries <c>TransportExtras.NyxUserAccessToken</c> for the proxy call. Stream chunk
    /// methods read it from the chunk's own activity; finalize is invoked from the
    /// <c>LlmReplyReadyEvent</c> path so the actor passes the event's reference activity
    /// here instead of a chunk.
    /// </param>
    Task<ConversationCardFinalizeResult> RunCardFinalizeAsync(
        ChatActivity referenceActivity,
        string cardId,
        string elementId,
        string finalText,
        bool finalTextDiffersFromLastFlushed,
        long sequence,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct);
}

/// <summary>
/// Outcome of <see cref="IConversationCardTurnRunner.RunCardCreateAsync"/>. The classification
/// flags drive the grain's fallback decision:
/// <list type="bullet">
/// <item>Pre-send failures (create call rejected before any chat-visible side effect): the
///   actor transitions to <c>CreationFailed</c> and falls back to the legacy text-edit sink
///   so the user still sees a reply. <see cref="IsRateLimited"/> /
///   <see cref="IsTableLimitExceeded"/> imply this path.</item>
/// <item>Post-send failures (create + send succeeded but the first stream-content write
///   failed — see <see cref="IsPostSendFailure"/>): an empty card is already visible in the
///   chat. Falling back to text-edit would produce a duplicate reply. The actor terminates
///   the turn at <c>Terminated</c> using the surfaced <see cref="CardId"/> /
///   <see cref="CardMessageId"/> and persists the partial-card terminal record. The runner
///   makes a best-effort settings patch to close streaming mode on the orphan card before
///   returning so the cursor does not blink forever.</item>
/// <item><see cref="IsCardUnavailable"/> on its own terminates the turn (no fallback).</item>
/// </list>
/// </summary>
public sealed record ConversationCardCreateResult(
    bool Success,
    string? CardId,
    string? CardMessageId,
    bool IsRateLimited,
    bool IsTableLimitExceeded,
    bool IsCardUnavailable,
    bool IsPostSendFailure,
    string ErrorCode,
    string ErrorSummary)
{
    public static ConversationCardCreateResult Succeeded(string cardId, string cardMessageId) =>
        new(true, cardId, cardMessageId, false, false, false, false, string.Empty, string.Empty);

    public static ConversationCardCreateResult Failed(
        string errorCode,
        string errorSummary,
        bool isRateLimited = false,
        bool isTableLimitExceeded = false,
        bool isCardUnavailable = false) =>
        new(false, null, null, isRateLimited, isTableLimitExceeded, isCardUnavailable, false, errorCode, errorSummary);

    /// <summary>
    /// Failure factory for the "card was already sent to the chat but the first
    /// element-content write failed" case. The actor must NOT fall back to text-edit
    /// (the orphan card is already visible) — it transitions the turn to <c>Terminated</c>
    /// and uses <paramref name="cardId"/> / <paramref name="cardMessageId"/> for the
    /// persisted partial-card record.
    /// </summary>
    public static ConversationCardCreateResult PostSendFailed(
        string cardId,
        string cardMessageId,
        string errorCode,
        string errorSummary,
        bool isRateLimited = false,
        bool isTableLimitExceeded = false,
        bool isCardUnavailable = false) =>
        new(false, cardId, cardMessageId, isRateLimited, isTableLimitExceeded, isCardUnavailable, true, errorCode, errorSummary);
}

/// <summary>
/// Outcome of <see cref="IConversationCardTurnRunner.RunCardStreamAsync"/>. Mid-stream
/// rate-limit (Lark <c>230020</c>) is recoverable — the grain skips the frame and continues.
/// Table-limit (<c>230099</c>/<c>11310</c>) and unavailability terminate the turn.
/// </summary>
public sealed record ConversationCardStreamResult(
    bool Success,
    bool IsRateLimited,
    bool IsTableLimitExceeded,
    bool IsCardUnavailable,
    string ErrorCode,
    string ErrorSummary)
{
    public static ConversationCardStreamResult Succeeded() =>
        new(true, false, false, false, string.Empty, string.Empty);

    public static ConversationCardStreamResult Failed(
        string errorCode,
        string errorSummary,
        bool isRateLimited = false,
        bool isTableLimitExceeded = false,
        bool isCardUnavailable = false) =>
        new(false, isRateLimited, isTableLimitExceeded, isCardUnavailable, errorCode, errorSummary);
}

/// <param name="Success">True only when both the optional final stream write AND the
/// streaming-mode close succeeded.</param>
/// <param name="FinalTextWritten">
/// True when the trailing element-content write either succeeded OR was skipped
/// (final text equals last flushed). False only when the runner attempted the trailing
/// write and it failed; lets the actor persist the visible-state text correctly when
/// success is false but the final text actually did land before the close-streaming-mode
/// failure.
/// </param>
public sealed record ConversationCardFinalizeResult(
    bool Success,
    bool FinalTextWritten,
    string ErrorCode,
    string ErrorSummary)
{
    public static ConversationCardFinalizeResult Succeeded() =>
        new(true, true, string.Empty, string.Empty);

    /// <summary>
    /// Failure factory. <paramref name="finalTextWritten"/> distinguishes between "trailing
    /// write failed; user sees stale interim" (false) and "trailing write succeeded but
    /// streaming-mode close failed; user sees the final text with a still-blinking cursor"
    /// (true).
    /// </summary>
    public static ConversationCardFinalizeResult Failed(string errorCode, string errorSummary, bool finalTextWritten = false) =>
        new(false, finalTextWritten, errorCode, errorSummary);
}

/// <summary>
/// No-op default. Every CardKit operation reports a transient failure that disables the
/// card path so the grain can fall back to the legacy text-edit sink. Production DI registers
/// a real implementation when CardKit is enabled.
/// </summary>
public sealed class NullConversationCardTurnRunner : IConversationCardTurnRunner
{
    public Task<ConversationCardCreateResult> RunCardCreateAsync(
        LlmReplyCardStreamChunkEvent chunk,
        string streamingElementId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        Task.FromResult(ConversationCardCreateResult.Failed(
            "no_card_runner",
            "no IConversationCardTurnRunner registered"));

    public Task<ConversationCardStreamResult> RunCardStreamAsync(
        LlmReplyCardStreamChunkEvent chunk,
        string cardId,
        string elementId,
        long sequence,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        Task.FromResult(ConversationCardStreamResult.Failed(
            "no_card_runner",
            "no IConversationCardTurnRunner registered"));

    public Task<ConversationCardFinalizeResult> RunCardFinalizeAsync(
        ChatActivity referenceActivity,
        string cardId,
        string elementId,
        string finalText,
        bool finalTextDiffersFromLastFlushed,
        long sequence,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        Task.FromResult(ConversationCardFinalizeResult.Failed(
            "no_card_runner",
            "no IConversationCardTurnRunner registered"));
}
