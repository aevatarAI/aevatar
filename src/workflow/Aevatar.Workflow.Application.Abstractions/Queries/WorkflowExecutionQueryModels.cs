namespace Aevatar.Workflow.Application.Abstractions.Queries;

public sealed record WorkflowAgentSummary(
    string Id,
    string Type,
    string Description);

public sealed class WorkflowActorSnapshot
{
    public string ActorId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string LastCommandId { get; set; } = string.Empty;
    public long StateVersion { get; set; }
    public string LastEventId { get; set; } = string.Empty;
    public DateTimeOffset LastUpdatedAt { get; set; }
    public bool? LastSuccess { get; set; }
    public string LastOutput { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public int TotalSteps { get; set; }
    public int RequestedSteps { get; set; }
    public int CompletedSteps { get; set; }
    public int RoleReplyCount { get; set; }
}

public sealed class WorkflowActorTimelineItem
{
    public DateTimeOffset Timestamp { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string StepType { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = [];
}

public sealed class WorkflowActorRelationNode
{
    public string NodeId { get; set; } = string.Empty;

    public string NodeType { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class WorkflowActorRelationItem
{
    public string EdgeId { get; set; } = string.Empty;

    public string FromNodeId { get; set; } = string.Empty;

    public string ToNodeId { get; set; } = string.Empty;

    public string RelationType { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class WorkflowActorRelationSubgraph
{
    public string RootNodeId { get; set; } = string.Empty;

    public List<WorkflowActorRelationNode> Nodes { get; set; } = [];

    public List<WorkflowActorRelationItem> Edges { get; set; } = [];
}

public sealed record WorkflowTopologyEdge(string Parent, string Child);

public enum WorkflowRunProjectionScope
{
    ActorShared = 0,
    RunIsolated = 1,
    Unknown = 99,
}

public enum WorkflowRunTopologySource
{
    RuntimeSnapshot = 0,
    Unknown = 99,
}

public enum WorkflowRunCompletionStatus
{
    Running = 0,
    Completed = 1,
    TimedOut = 2,
    Failed = 3,
    Stopped = 4,
    NotFound = 5,
    Disabled = 6,
    Unknown = 99,
}

public sealed class WorkflowRunReport
{
    public string ReportVersion { get; set; } = "1.0";
    public WorkflowRunProjectionScope ProjectionScope { get; set; } = WorkflowRunProjectionScope.Unknown;
    public WorkflowRunTopologySource TopologySource { get; set; } = WorkflowRunTopologySource.Unknown;
    public WorkflowRunCompletionStatus CompletionStatus { get; set; } = WorkflowRunCompletionStatus.Unknown;
    public string WorkflowName { get; set; } = string.Empty;
    public string RootActorId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public long StateVersion { get; set; }
    public string LastEventId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double DurationMs { get; set; }
    public bool? Success { get; set; }
    public string Input { get; set; } = string.Empty;
    public string FinalOutput { get; set; } = string.Empty;
    public string FinalError { get; set; } = string.Empty;
    public List<WorkflowRunTopologyEdge> Topology { get; set; } = [];
    public List<WorkflowRunStepTrace> Steps { get; set; } = [];
    public List<WorkflowRunRoleReply> RoleReplies { get; set; } = [];
    public List<WorkflowRunTimelineEvent> Timeline { get; set; } = [];
    public WorkflowRunStatistics Summary { get; set; } = new();
}

public sealed class WorkflowRunStatistics
{
    public int TotalSteps { get; set; }
    public int RequestedSteps { get; set; }
    public int CompletedSteps { get; set; }
    public int RoleReplyCount { get; set; }
    public Dictionary<string, int> StepTypeCounts { get; set; } = [];
}

public sealed class WorkflowRunStepTrace
{
    public string StepId { get; set; } = string.Empty;
    public string StepType { get; set; } = string.Empty;
    public string TargetRole { get; set; } = string.Empty;
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool? Success { get; set; }
    public string WorkerId { get; set; } = string.Empty;
    public string OutputPreview { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public Dictionary<string, string> RequestParameters { get; set; } = [];
    public Dictionary<string, string> CompletionMetadata { get; set; } = [];
    public double? DurationMs => RequestedAt.HasValue && CompletedAt.HasValue
        ? Math.Max(0, (CompletedAt.Value - RequestedAt.Value).TotalMilliseconds)
        : null;
}

public sealed class WorkflowRunRoleReply
{
    public DateTimeOffset Timestamp { get; set; }
    public string RoleId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int ContentLength { get; set; }
}

public sealed class WorkflowRunTimelineEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string StepType { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = [];
}

public sealed record WorkflowRunTopologyEdge(string Parent, string Child);
