namespace Aevatar.AI.Abstractions.ToolProviders;

// Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
//   Old pattern: remote approval submit/status lived behind a blocking IToolApprovalHandler poll loop.
//   New principle: remote approval is a narrow command/status port; RoleGAgent owns continuation state.
/// <summary>Remote approval command/status port.</summary>
public interface IRemoteToolApprovalPort
{
    Task<RemoteToolApprovalSubmission> SubmitAsync(RemoteToolApprovalRequest request, CancellationToken ct);

    Task<RemoteToolApprovalStatusSnapshot> GetStatusAsync(RemoteToolApprovalStatusQuery query, CancellationToken ct);

    Task<RemoteToolApprovalDecisionResult> DecideAsync(RemoteToolApprovalDecision decision, CancellationToken ct);
}

public interface IRemoteToolApprovalNotificationPort
{
    Task<RemoteToolApprovalNotificationSupport> CheckSupportAsync(
        AgentToolExecutionContext toolContext,
        CancellationToken ct) =>
        Task.FromResult(RemoteToolApprovalNotificationSupport.SupportedResult);

    Task NotifyAsync(RemoteToolApprovalNotification notification, CancellationToken ct);
}

public sealed record RemoteToolApprovalRequest(
    string RequestId,
    string ToolName,
    string ToolCallId,
    string ArgumentsJson,
    ToolApprovalMode ApprovalMode,
    bool IsDestructive);

public sealed record RemoteToolApprovalSubmission(
    string RemoteApprovalId,
    DateTimeOffset? ExpiresAt);

public sealed record RemoteToolApprovalNotification(
    RemoteToolApprovalRequest Request,
    RemoteToolApprovalSubmission Submission,
    AgentToolExecutionContext ToolContext);

public sealed record RemoteToolApprovalNotificationSupport(
    bool Supported,
    string? Reason = null)
{
    public static RemoteToolApprovalNotificationSupport SupportedResult { get; } = new(true);

    public static RemoteToolApprovalNotificationSupport Unsupported(string reason) =>
        new(false, reason);
}

public sealed record RemoteToolApprovalStatusQuery(
    string RequestId,
    string RemoteApprovalId);

public sealed record RemoteToolApprovalDecision(
    string RequestId,
    bool Approved);

public sealed record RemoteToolApprovalDecisionResult(
    bool Succeeded,
    string? ErrorKey = null,
    string? Detail = null,
    int? Status = null);

public sealed record RemoteToolApprovalStatusSnapshot(
    RemoteToolApprovalStatus Status,
    string? Reason = null,
    DateTimeOffset? ExpiresAt = null);

public enum RemoteToolApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Cancelled = 4,
    Unknown = 5,
}
