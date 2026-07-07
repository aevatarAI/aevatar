namespace Aevatar.Studio.Application.Studio.WorkflowBoards;

public static class WorkflowBoardSnapshotRequestLimits
{
    public const int DefaultMemberRows = 20;
    public const int MaxMemberRows = 100;
}

public sealed record WorkflowBoardSnapshotRequest(
    string ScopeId,
    string? TeamId = null,
    string? MemberId = null,
    int? Take = null);

public sealed record WorkflowBoardSnapshot(
    string ScopeId,
    DateTimeOffset GeneratedAt,
    string Watermark,
    WorkflowBoardSnapshotCounts Counts,
    IReadOnlyList<WorkflowBoardTeamSnapshot> Teams,
    DateTimeOffset? LastNodeUpdatedAt = null);

public sealed record WorkflowBoardSnapshotCounts(
    int Running,
    int Waiting,
    int Failed,
    int Retrying,
    int Completed);

public sealed record WorkflowBoardTeamSnapshot(
    string TeamId,
    string TeamName,
    int? TotalMemberCount,
    IReadOnlyList<WorkflowBoardMemberSnapshot> Members);

public sealed record WorkflowBoardMemberSnapshot(
    string MemberId,
    string DisplayName,
    WorkflowBoardExecutionAvailability ExecutionAvailability,
    IReadOnlyList<WorkflowBoardCompletedNode> CompletedNodes,
    IReadOnlyList<WorkflowBoardPendingNode> PendingNodes,
    IReadOnlyList<WorkflowBoardFailedNode> FailedNodes)
{
    public string? WorkflowId { get; init; }
    public string? WorkflowName { get; init; }
    public string? PublishedServiceId { get; init; }
    public string? ActorId { get; init; }
    public string? RoleSummary { get; init; }
    public string? CurrentExecutionId { get; init; }
    public string? ExecutionRevision { get; init; }
    public WorkflowBoardMemberExecutionStatus ExecutionStatus { get; init; }
    public WorkflowBoardMemberProgress? Progress { get; init; }
    public WorkflowBoardCurrentNode? CurrentNode { get; init; }
    public DateTimeOffset? LastNodeUpdatedAt { get; init; }
}

public sealed record WorkflowBoardMemberProgress(
    int CompletedSteps,
    int TotalSteps);

public sealed record WorkflowBoardCurrentNode(
    string NodeId,
    string Name,
    WorkflowBoardCurrentNodeStatus Status,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? UpdatedAt = null,
    long? DurationMs = null);

public sealed record WorkflowBoardCompletedNode(
    string NodeId,
    string Name,
    DateTimeOffset? CompletedAt = null,
    long? DurationMs = null);

public sealed record WorkflowBoardPendingNode(
    string NodeId,
    string Name,
    WorkflowBoardPendingNodeStatus Status,
    string? Reason = null);

public sealed record WorkflowBoardFailedNode(
    string NodeId,
    string Name,
    DateTimeOffset? FailedAt = null);

public enum WorkflowBoardCurrentNodeStatus
{
    Unknown = 0,
    Running,
    Waiting,
    Pending,
    Failed,
    Completed,
}

public enum WorkflowBoardPendingNodeStatus
{
    Unknown = 0,
    Waiting,
    Pending,
    Queued,
}

public enum WorkflowBoardExecutionAvailability
{
    Unknown = 0,
    Available,
    Unavailable,
    PendingBackendContract,
}

public enum WorkflowBoardMemberExecutionStatus
{
    Unknown = 0,
    Running,
    Waiting,
    Failed,
    Retrying,
    Completed,
    Stopped,
}

public sealed record WorkflowBoardRosterTeam(
    string TeamId,
    string ScopeId,
    string DisplayName,
    bool IsArchived,
    int? TotalMemberCount,
    DateTimeOffset? UpdatedAt = null);

public sealed record WorkflowBoardRosterMember(
    string MemberId,
    string ScopeId,
    string? TeamId,
    string DisplayName,
    bool IsArchived,
    string? PublishedServiceId = null,
    string? WorkflowId = null,
    string? WorkflowName = null,
    string? ActorId = null,
    string? RoleSummary = null,
    DateTimeOffset? UpdatedAt = null);

public sealed record WorkflowBoardExecutionLookup(
    string ScopeId,
    string TeamId,
    string MemberId,
    string? WorkflowId,
    string? PublishedServiceId,
    string? ActorId);

public sealed record WorkflowBoardExecutionSnapshot(
    WorkflowBoardExecutionAvailability Availability,
    IReadOnlyList<WorkflowBoardCompletedNode> CompletedNodes,
    IReadOnlyList<WorkflowBoardPendingNode> PendingNodes,
    IReadOnlyList<WorkflowBoardFailedNode> FailedNodes)
{
    public string? CurrentExecutionId { get; init; }
    public WorkflowBoardMemberExecutionStatus ExecutionStatus { get; init; }
    public WorkflowBoardCurrentNode? CurrentNode { get; init; }
    public DateTimeOffset? LastNodeUpdatedAt { get; init; }
    public WorkflowBoardExecutionSummary? Summary { get; init; }
    public string? Revision { get; init; }
}

public sealed record WorkflowBoardExecutionSummary(
    int? CompletedSteps,
    int? RunningNodes,
    int? WaitingOrPendingNodes,
    int? FailedNodes,
    int? DefinitionStepCount);

public interface IWorkflowBoardSnapshotQueryPort
{
    Task<WorkflowBoardSnapshot> GetSnapshotAsync(
        WorkflowBoardSnapshotRequest request,
        CancellationToken ct = default);
}

public interface IWorkflowBoardRosterQueryPort
{
    Task<IReadOnlyList<WorkflowBoardRosterTeam>> ListTeamsAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowBoardRosterMember>> ListMembersAsync(
        string scopeId,
        string? teamId,
        int take,
        CancellationToken ct = default);

    Task<WorkflowBoardRosterTeam?> GetTeamAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default);

    Task<WorkflowBoardRosterMember?> GetMemberAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default);
}

public interface IWorkflowBoardExecutionQueryPort
{
    Task<WorkflowBoardExecutionSnapshot?> GetCurrentExecutionAsync(
        WorkflowBoardExecutionLookup lookup,
        CancellationToken ct = default);
}

public interface IWorkflowBoardClock
{
    DateTimeOffset GetUtcNow();
}

public sealed class WorkflowBoardSnapshotRequestException : InvalidOperationException
{
    public WorkflowBoardSnapshotRequestException(string message)
        : base(message)
    {
    }
}

public sealed class WorkflowBoardReadModelUnavailableException : InvalidOperationException
{
    public WorkflowBoardReadModelUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
