using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Runs one bot turn inside <see cref="ConversationGAgent"/>. The seam lets the grain stay
/// channel-agnostic while deferring actual <see cref="IBot"/> invocation + outbound send to
/// runtime-specific implementations (middleware pipeline + adapter outbound port).
/// </summary>
/// <remarks>
/// The grain assumes turn-serial execution and will call this seam at most once per dedup-admitted
/// inbound activity or proactive command. Implementations must be safe under that single-threaded
/// invariant and must not return partial state: either success with a <see cref="ConversationTurnResult.SentActivityId"/>
/// or a well-formed failure.
/// </remarks>
public interface IConversationTurnRunner
{
    /// <summary>
    /// Executes one bot turn for an inbound activity.
    /// </summary>
    Task<ConversationTurnResult> RunInboundAsync(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct);

    /// <summary>
    /// Executes the outbound leg after an asynchronous LLM reply has been generated.
    /// </summary>
    Task<ConversationTurnResult> RunLlmReplyAsync(
        LlmReplyReadyEvent reply,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct);

    /// <summary>
    /// Executes one bot turn for a proactive continue command.
    /// </summary>
    Task<ConversationTurnResult> RunContinueAsync(ConversationContinueRequestedEvent command, CancellationToken ct);

    /// <summary>
    /// Delivers one progressive streaming chunk to the downstream platform. If
    /// <paramref name="currentPlatformMessageId"/> is <c>null</c>, the chunk is dispatched as the
    /// initial placeholder send; otherwise it is dispatched as an edit targeting that upstream
    /// platform message. Only invoked by <see cref="ConversationGAgent"/> while it holds the reply
    /// token in-memory.
    /// </summary>
    Task<ConversationStreamChunkResult> RunStreamChunkAsync(
        LlmReplyStreamChunkEvent chunk,
        string? currentPlatformMessageId,
        NyxRelayTextOperationKind operation,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct);

    /// <summary>
    /// Post-reply hook invoked once the user-visible reply has landed via a path the runner does
    /// not orchestrate end-to-end — currently the streaming completion path in
    /// <see cref="ConversationGAgent"/> finalizes its own reply through <c>RunStreamChunkAsync</c>
    /// + persistence, never touching <see cref="RunLlmReplyAsync"/>. Implementations use this hook
    /// to do platform-specific post-reply housekeeping (e.g. swap the Lark "Typing" reaction to
    /// "DONE"). Default no-op so adapters that have nothing to do here need no opt-in.
    /// </summary>
    Task OnReplyDeliveredAsync(ChatActivity activity, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Outcome of one progressive streaming chunk dispatch.
/// </summary>
// Refactor (iter1535/cluster-issue-1535):
//   Old pattern: streaming chunk failures exposed only string summaries plus edit_unsupported.
//   New principle: turn runners return typed failure classification and sanitized adapter diagnostics.
public sealed record ConversationStreamChunkResult(
    bool Success,
    string? PlatformMessageId,
    bool EditUnsupported,
    string ErrorCode,
    string ErrorSummary,
    FailureKind FailureKind,
    TimeSpan? RetryAfter,
    int HttpStatus,
    string RawErrorKey,
    int RawErrorCode)
{
    public static ConversationStreamChunkResult Succeeded(string? platformMessageId) =>
        new(
            true,
            platformMessageId,
            false,
            string.Empty,
            string.Empty,
            FailureKind.Unspecified,
            null,
            0,
            string.Empty,
            0);

    public static ConversationStreamChunkResult Failed(
        string errorCode,
        string errorSummary,
        bool editUnsupported = false,
        FailureKind failureKind = FailureKind.Unspecified,
        TimeSpan? retryAfter = null,
        int httpStatus = 0,
        string? rawErrorKey = null,
        int rawErrorCode = 0) =>
        new(
            false,
            null,
            editUnsupported,
            errorCode,
            errorSummary,
            failureKind,
            retryAfter,
            httpStatus,
            string.IsNullOrWhiteSpace(rawErrorKey) ? string.Empty : rawErrorKey.Trim(),
            rawErrorCode);
}

// Refactor (iter17/cluster-038):
//   Old pattern: relay reply/user credentials both rode on persisted ChatActivity transport extras.
//   New principle: same-turn Nyx credentials are runtime-only context while durable activity copies stay sanitized.
public sealed record NyxRelayReplyTokenContext(
    string CorrelationId,
    string ReplyToken,
    string ReplyMessageId,
    DateTimeOffset ExpiresAtUtc,
    string? NyxUserAccessToken = null);

// Refactor (iter17/cluster-038):
//   Old pattern: concrete turn runners re-read transient relay credentials from durable activity payloads.
//   New principle: ConversationGAgent passes non-persisted credentials explicitly through per-turn runtime context.
public sealed record ConversationTurnRuntimeContext(
    NyxRelayReplyTokenContext? NyxRelayReplyToken,
    string? NyxUserAccessToken = null,
    // True when this inbound activity replies to one of the bot's own prior messages. Computed by
    // ConversationGAgent (the owner of the bot-sent message ledger) and consumed by the channel
    // runner's group-chat admission gate so a thread reply counts as addressing the bot without a
    // re-@-mention. One-way: the runner never writes it back.
    bool IsReplyToBot = false)
{
    public static ConversationTurnRuntimeContext Empty { get; } = new(NyxRelayReplyToken: null);
}

/// <summary>
/// Describes the outcome of one bot turn (either inbound-activity-driven or proactive-command-driven).
/// </summary>
// Refactor (iter1535/cluster-issue-1535):
//   Old pattern: turn failures forced downstream actors to infer retry intent from error strings.
//   New principle: bot turn outcomes carry the typed failure kind used by continuation policy.
public sealed record ConversationTurnResult(
    bool Success,
    string SentActivityId,
    MessageContent Outbound,
    string AuthPrincipal,
    OutboundDeliveryContext? OutboundDelivery,
    string ErrorCode,
    string ErrorSummary,
    FailureKind FailureKind,
    TimeSpan? RetryAfter,
    NeedsLlmReplyEvent? LlmReplyRequest,
    NeedsWorkflowDraftRunEvent? WorkflowDraftRunRequest,
    // Typed business outcome of a /clear turn: the conversation actor (the sole
    // owner of retained history) persists ConversationRetainedHistoryClearedEvent
    // and resets its transcript window when this is set.
    bool RetainedHistoryClearRequested = false)
{
    /// <summary>
    /// Success factory.
    /// </summary>
    public static ConversationTurnResult Sent(
        string sentActivityId,
        MessageContent outbound,
        string authPrincipal,
        OutboundDeliveryContext? outboundDelivery = null) =>
        new(
            true,
            sentActivityId,
            outbound,
            authPrincipal,
            outboundDelivery,
            string.Empty,
            string.Empty,
            FailureKind.Unspecified,
            null,
            null,
            null);

    /// <summary>
    /// Deferred-reply success factory.
    /// </summary>
    public static ConversationTurnResult LlmReplyRequested(NeedsLlmReplyEvent request, string authPrincipal = "bot") =>
        new(
            true,
            string.Empty,
            new MessageContent(),
            authPrincipal,
            null,
            string.Empty,
            string.Empty,
            FailureKind.Unspecified,
            null,
            request?.Clone() ?? throw new ArgumentNullException(nameof(request)),
            null);

    public static ConversationTurnResult WorkflowDraftRunRequested(NeedsWorkflowDraftRunEvent request, string authPrincipal = "bot") =>
        new(
            true,
            string.Empty,
            new MessageContent(),
            authPrincipal,
            null,
            string.Empty,
            string.Empty,
            FailureKind.Unspecified,
            null,
            null,
            request?.Clone() ?? throw new ArgumentNullException(nameof(request)));

    /// <summary>
    /// Ignored-inbound success factory. Used when the turn runner deliberately produces no
    /// reply for a well-formed inbound activity (for example an unrecognized card action
    /// that should not be promoted into an LLM turn). The <paramref name="reasonCode"/>
    /// is stamped into <see cref="SentActivityId"/> as <c>ignored:{reasonCode}:{activityId}</c>
    /// so observers can filter these no-op completions without guessing from an empty id.
    /// </summary>
    public static ConversationTurnResult Ignored(string reasonCode, string activityId, string detail = "") =>
        new(
            true,
            string.IsNullOrWhiteSpace(activityId)
                ? $"ignored:{reasonCode}"
                : $"ignored:{reasonCode}:{activityId}",
            new MessageContent(),
            "bot",
            null,
            string.Empty,
            detail ?? string.Empty,
            FailureKind.Unspecified,
            null,
            null,
            null);

    /// <summary>
    /// Transient failure factory.
    /// </summary>
    public static ConversationTurnResult TransientFailure(string errorCode, string errorSummary, TimeSpan? retryAfter = null) =>
        new(
            false,
            string.Empty,
            new MessageContent(),
            string.Empty,
            null,
            errorCode,
            errorSummary,
            FailureKind.TransientAdapterError,
            retryAfter,
            null,
            null);

    /// <summary>
    /// Permanent failure factory.
    /// </summary>
    public static ConversationTurnResult PermanentFailure(string errorCode, string errorSummary) =>
        new(
            false,
            string.Empty,
            new MessageContent(),
            string.Empty,
            null,
            errorCode,
            errorSummary,
            FailureKind.PermanentAdapterError,
            null,
            null,
            null);
}

/// <summary>
/// No-op default that every turn reports transient failure. Production DI registers a real implementation.
/// </summary>
public sealed class NullConversationTurnRunner : IConversationTurnRunner
{
    /// <inheritdoc />
    public Task<ConversationTurnResult> RunInboundAsync(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        Task.FromResult(ConversationTurnResult.TransientFailure("no_runner", "no IConversationTurnRunner registered"));

    /// <inheritdoc />
    public Task<ConversationTurnResult> RunLlmReplyAsync(
        LlmReplyReadyEvent reply,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        Task.FromResult(ConversationTurnResult.TransientFailure("no_runner", "no IConversationTurnRunner registered"));

    /// <inheritdoc />
    public Task<ConversationTurnResult> RunContinueAsync(ConversationContinueRequestedEvent command, CancellationToken ct) =>
        Task.FromResult(ConversationTurnResult.TransientFailure("no_runner", "no IConversationTurnRunner registered"));

    /// <inheritdoc />
    public Task<ConversationStreamChunkResult> RunStreamChunkAsync(
        LlmReplyStreamChunkEvent chunk,
        string? currentPlatformMessageId,
        NyxRelayTextOperationKind operation,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        Task.FromResult(ConversationStreamChunkResult.Failed("no_runner", "no IConversationTurnRunner registered"));
}
