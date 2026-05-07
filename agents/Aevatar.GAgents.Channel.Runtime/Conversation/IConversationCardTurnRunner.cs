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
        LlmReplyStreamChunkEvent chunk,
        string streamingElementId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct);

    /// <summary>
    /// Streams the latest accumulated text into the existing card element. Sequence is
    /// pre-incremented by the grain. Lark rejects stale sequences deterministically.
    /// </summary>
    Task<ConversationCardStreamResult> RunCardStreamAsync(
        LlmReplyStreamChunkEvent chunk,
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
    Task<ConversationCardFinalizeResult> RunCardFinalizeAsync(
        string cardId,
        string elementId,
        string finalText,
        bool finalTextDiffersFromLastFlushed,
        long sequence,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct);
}

/// <summary>
/// Outcome of <see cref="IConversationCardTurnRunner.RunCardCreateAsync"/>. The error
/// classification flags drive the grain's fallback decision: <see cref="IsRateLimited"/>
/// and <see cref="IsTableLimitExceeded"/> route the turn to the legacy text-edit sink;
/// <see cref="IsCardUnavailable"/> terminates the turn at <c>Terminated</c>.
/// </summary>
public sealed record ConversationCardCreateResult(
    bool Success,
    string? CardId,
    string? CardMessageId,
    bool IsRateLimited,
    bool IsTableLimitExceeded,
    bool IsCardUnavailable,
    string ErrorCode,
    string ErrorSummary)
{
    public static ConversationCardCreateResult Succeeded(string cardId, string cardMessageId) =>
        new(true, cardId, cardMessageId, false, false, false, string.Empty, string.Empty);

    public static ConversationCardCreateResult Failed(
        string errorCode,
        string errorSummary,
        bool isRateLimited = false,
        bool isTableLimitExceeded = false,
        bool isCardUnavailable = false) =>
        new(false, null, null, isRateLimited, isTableLimitExceeded, isCardUnavailable, errorCode, errorSummary);
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

public sealed record ConversationCardFinalizeResult(
    bool Success,
    string ErrorCode,
    string ErrorSummary)
{
    public static ConversationCardFinalizeResult Succeeded() =>
        new(true, string.Empty, string.Empty);

    public static ConversationCardFinalizeResult Failed(string errorCode, string errorSummary) =>
        new(false, errorCode, errorSummary);
}

/// <summary>
/// No-op default. Every CardKit operation reports a transient failure that disables the
/// card path so the grain can fall back to the legacy text-edit sink. Production DI registers
/// a real implementation when CardKit is enabled.
/// </summary>
public sealed class NullConversationCardTurnRunner : IConversationCardTurnRunner
{
    public Task<ConversationCardCreateResult> RunCardCreateAsync(
        LlmReplyStreamChunkEvent chunk,
        string streamingElementId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        Task.FromResult(ConversationCardCreateResult.Failed(
            "no_card_runner",
            "no IConversationCardTurnRunner registered"));

    public Task<ConversationCardStreamResult> RunCardStreamAsync(
        LlmReplyStreamChunkEvent chunk,
        string cardId,
        string elementId,
        long sequence,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        Task.FromResult(ConversationCardStreamResult.Failed(
            "no_card_runner",
            "no IConversationCardTurnRunner registered"));

    public Task<ConversationCardFinalizeResult> RunCardFinalizeAsync(
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
