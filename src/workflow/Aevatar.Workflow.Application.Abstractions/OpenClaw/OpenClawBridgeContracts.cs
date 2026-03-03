namespace Aevatar.Workflow.Application.Abstractions.OpenClaw;

public sealed record OpenClawBridgeExecutionRequest
{
    public string? Prompt { get; init; }
    public string? Message { get; init; }
    public string? Text { get; init; }
    public string? Workflow { get; init; }
    public string? ActorId { get; init; }
    public string? SessionId { get; init; }
    public string? ChannelId { get; init; }
    public string? UserId { get; init; }
    public string? MessageId { get; init; }
    public string? IdempotencyKey { get; init; }
    public IReadOnlyList<string>? WorkflowYamls { get; init; }
    public string? CallbackUrl { get; init; }
    public string? CallbackToken { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public string DefaultWorkflowName { get; init; } = string.Empty;
    public bool EnableIdempotency { get; init; } = true;
    public int IdempotencyTtlHours { get; init; } = 24;
    public IReadOnlyList<string>? CallbackAllowedHosts { get; init; }
    public string CallbackAuthHeaderName { get; init; } = "Authorization";
    public string CallbackAuthScheme { get; init; } = "Bearer";
    public int CallbackTimeoutMs { get; init; } = 5000;
    public int CallbackMaxAttempts { get; init; } = 1;
    public int CallbackRetryDelayMs { get; init; } = 300;
}

public sealed record OpenClawBridgeExecutionResult
{
    public required int StatusCode { get; init; }
    public bool Accepted { get; init; }
    public bool Replayed { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string SessionKey { get; init; } = string.Empty;
    public string ChannelId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string ActorId { get; init; } = string.Empty;
    public string CommandId { get; init; } = string.Empty;
    public string WorkflowName { get; init; } = string.Empty;

    public static OpenClawBridgeExecutionResult AcceptedResult(
        string actorId,
        string commandId,
        string workflowName,
        string correlationId,
        string idempotencyKey,
        string sessionKey,
        string channelId,
        string userId,
        bool replayed = false) =>
        new()
        {
            StatusCode = 202,
            Accepted = true,
            Replayed = replayed,
            ActorId = actorId,
            CommandId = commandId,
            WorkflowName = workflowName,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            SessionKey = sessionKey,
            ChannelId = channelId,
            UserId = userId,
        };

    public static OpenClawBridgeExecutionResult ErrorResult(
        int statusCode,
        string code,
        string message,
        string correlationId = "",
        string idempotencyKey = "",
        string sessionKey = "",
        string channelId = "",
        string userId = "",
        string actorId = "",
        string commandId = "",
        string workflowName = "") =>
        new()
        {
            StatusCode = statusCode,
            Code = code,
            Message = message,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            SessionKey = sessionKey,
            ChannelId = channelId,
            UserId = userId,
            ActorId = actorId,
            CommandId = commandId,
            WorkflowName = workflowName,
        };
}

public interface IOpenClawBridgeOrchestrationService
{
    Task<OpenClawBridgeExecutionResult> ExecuteAsync(
        OpenClawBridgeExecutionRequest request,
        CancellationToken ct = default);
}

public sealed record OpenClawBridgeReceiptDispatchRequest
{
    public required string CallbackUrl { get; init; }
    public string CallbackToken { get; init; } = string.Empty;
    public required string EventId { get; init; }
    public required long Sequence { get; init; }
    public required string EventType { get; init; }
    public required string CorrelationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string SessionKey { get; init; }
    public required string ChannelId { get; init; }
    public required string UserId { get; init; }
    public required string MessageId { get; init; }
    public string ActorId { get; init; } = string.Empty;
    public string CommandId { get; init; } = string.Empty;
    public required string WorkflowName { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public object? Payload { get; init; }
    public string AuthHeaderName { get; init; } = "Authorization";
    public string AuthScheme { get; init; } = "Bearer";
    public int TimeoutMs { get; init; } = 5000;
    public int MaxAttempts { get; init; } = 1;
    public int RetryDelayMs { get; init; } = 300;
}

public interface IOpenClawBridgeReceiptDispatcher
{
    Task DispatchAsync(
        OpenClawBridgeReceiptDispatchRequest request,
        CancellationToken ct = default);
}

public static class OpenClawIdempotencyStatuses
{
    public const string Pending = "pending";
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public enum OpenClawIdempotencyAcquireStatus
{
    Acquired = 0,
    ExistingPending = 1,
    ExistingStarted = 2,
    ExistingCompleted = 3,
    ExistingFailed = 4,
}

public sealed record OpenClawIdempotencyAcquireRequest(
    string IdempotencyKey,
    string SessionKey,
    string CorrelationId,
    string ActorId,
    string WorkflowName,
    string ChannelId,
    string UserId,
    string MessageId,
    int TtlHours);

public sealed record OpenClawIdempotencyAcquireResult(
    OpenClawIdempotencyAcquireStatus Status,
    OpenClawIdempotencyRecord? Record);

public sealed record OpenClawIdempotencyRecord
{
    public required string IdempotencyKey { get; init; }
    public required string SessionKey { get; init; }
    public required string CorrelationId { get; init; }
    public required string ActorId { get; init; }
    public required string WorkflowName { get; init; }
    public required string ChannelId { get; init; }
    public required string UserId { get; init; }
    public required string MessageId { get; init; }
    public required string Status { get; init; }
    public required long CreatedAtUnixMs { get; init; }
    public required long UpdatedAtUnixMs { get; init; }
    public required long ExpiresAtUnixMs { get; init; }
    public string CommandId { get; init; } = string.Empty;
    public string LastErrorCode { get; init; } = string.Empty;
    public string LastErrorMessage { get; init; } = string.Empty;

    public bool IsExpired(long nowUnixMs) =>
        ExpiresAtUnixMs > 0 && ExpiresAtUnixMs <= nowUnixMs;
}

public interface IOpenClawIdempotencyStore
{
    Task<OpenClawIdempotencyAcquireResult> AcquireAsync(
        OpenClawIdempotencyAcquireRequest request,
        CancellationToken ct = default);

    Task MarkStartedAsync(
        string idempotencyKey,
        string actorId,
        string commandId,
        string workflowName,
        CancellationToken ct = default);

    Task MarkCompletedAsync(
        string idempotencyKey,
        bool success,
        string errorCode,
        string errorMessage,
        CancellationToken ct = default);
}
