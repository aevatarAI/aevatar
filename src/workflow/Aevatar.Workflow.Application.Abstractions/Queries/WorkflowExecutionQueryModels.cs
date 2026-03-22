namespace Aevatar.Workflow.Application.Abstractions.Queries;

public sealed record WorkflowAgentSummary(
    string Id,
    string Type,
    string Description);

public sealed class WorkflowCatalogItem
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string GroupLabel { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string Source { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = string.Empty;
    public bool ShowInLibrary { get; set; }
    public bool IsPrimitiveExample { get; set; }
    public bool RequiresLlmProvider { get; set; }
    public List<string> Primitives { get; set; } = [];
}

public sealed class WorkflowCatalogRole
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public float? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxToolRounds { get; set; }
    public int? MaxHistoryMessages { get; set; }
    public int? StreamBufferCapacity { get; set; }
    public List<string> EventModules { get; set; } = [];
    public string EventRoutes { get; set; } = string.Empty;
    public List<string> Connectors { get; set; } = [];
}

public sealed class WorkflowCatalogChildStep
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string TargetRole { get; set; } = string.Empty;
}

public sealed class WorkflowCatalogStep
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string TargetRole { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = [];
    public string Next { get; set; } = string.Empty;
    public Dictionary<string, string> Branches { get; set; } = [];
    public List<WorkflowCatalogChildStep> Children { get; set; } = [];
}

public sealed class WorkflowCatalogEdge
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class WorkflowCatalogDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool ClosedWorldMode { get; set; }
    public List<WorkflowCatalogRole> Roles { get; set; } = [];
    public List<WorkflowCatalogStep> Steps { get; set; } = [];
}

public sealed class WorkflowCatalogItemDetail
{
    public WorkflowCatalogItem Catalog { get; set; } = new();
    public string Yaml { get; set; } = string.Empty;
    public WorkflowCatalogDefinition Definition { get; set; } = new();
    public List<WorkflowCatalogEdge> Edges { get; set; } = [];
}
public enum WorkflowActorGraphDirection
{
    Outbound = 0,
    Inbound = 1,
    Both = 2,
}

public sealed class WorkflowActorGraphQueryOptions
{
    public WorkflowActorGraphDirection Direction { get; set; } = WorkflowActorGraphDirection.Both;

    public IReadOnlyList<string> EdgeTypes { get; set; } = [];
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

public sealed class WorkflowRunExportDocument
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
    public Dictionary<string, string> CompletionAnnotations { get; set; } = [];
    public string NextStepId { get; set; } = string.Empty;
    public string BranchKey { get; set; } = string.Empty;
    public string AssignedVariable { get; set; } = string.Empty;
    public string AssignedValue { get; set; } = string.Empty;
    public string SuspensionType { get; set; } = string.Empty;
    public string SuspensionPrompt { get; set; } = string.Empty;
    public int? SuspensionTimeoutSeconds { get; set; }
    public string RequestedVariableName { get; set; } = string.Empty;
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
