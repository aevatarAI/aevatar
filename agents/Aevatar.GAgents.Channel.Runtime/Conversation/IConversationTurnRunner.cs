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
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct);
}

/// <summary>
/// Outcome of one progressive streaming chunk dispatch.
/// </summary>
public sealed record ConversationStreamChunkResult(
    bool Success,
    string? PlatformMessageId,
    bool EditUnsupported,
    string ErrorCode,
    string ErrorSummary)
{
    public static ConversationStreamChunkResult Succeeded(string? platformMessageId) =>
        new(true, platformMessageId, false, string.Empty, string.Empty);

    public static ConversationStreamChunkResult Failed(
        string errorCode,
        string errorSummary,
        bool editUnsupported = false) =>
        new(false, null, editUnsupported, errorCode, errorSummary);
}

public sealed record NyxRelayReplyTokenContext(
    string CorrelationId,
    string ReplyToken,
    string ReplyMessageId,
    DateTimeOffset ExpiresAtUtc);

public sealed record ConversationTurnRuntimeContext(NyxRelayReplyTokenContext? NyxRelayReplyToken)
{
    public static ConversationTurnRuntimeContext Empty { get; } = new(NyxRelayReplyToken: null);
}

/// <summary>
/// Describes the outcome of one bot turn (either inbound-activity-driven or proactive-command-driven).
/// </summary>
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
    NeedsLlmReplyEvent? LlmReplyRequest)
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
            request?.Clone() ?? throw new ArgumentNullException(nameof(request)));

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
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        Task.FromResult(ConversationStreamChunkResult.Failed("no_runner", "no IConversationTurnRunner registered"));
}
