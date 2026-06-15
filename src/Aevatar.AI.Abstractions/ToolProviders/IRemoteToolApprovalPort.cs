namespace Aevatar.AI.Abstractions.ToolProviders;

// Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
//   Old pattern: remote approval submit/status lived behind a blocking IToolApprovalHandler poll loop.
//   New principle: remote approval is a narrow submit/status port; RoleGAgent owns continuation state.
/// <summary>Remote approval submit/status port; refactor helper, no behavior change.</summary>
public interface IRemoteToolApprovalPort
{
    Task<RemoteToolApprovalSubmission> SubmitAsync(RemoteToolApprovalRequest request, CancellationToken ct);

    Task<RemoteToolApprovalStatusSnapshot> GetStatusAsync(RemoteToolApprovalStatusQuery query, CancellationToken ct);
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

public sealed record RemoteToolApprovalStatusQuery(
    string RequestId,
    string RemoteApprovalId);

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
    Unknown = 4,
}
