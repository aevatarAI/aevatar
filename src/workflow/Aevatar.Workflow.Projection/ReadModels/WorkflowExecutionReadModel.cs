using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.ReadModels;

public enum WorkflowExecutionProjectionScope
{
    ActorShared = 0,
}

public enum WorkflowExecutionTopologySource
{
    RuntimeSnapshot = 0,
}

public enum WorkflowExecutionCompletionStatus
{
    Running = 0,
    Completed = 1,
    TimedOut = 2,
    Failed = 3,
    Stopped = 4,
    NotFound = 5,
    Disabled = 6,
    WaitingForSignal = 7,
    Unknown = 99,
}

/// <summary>
/// Read model for one workflow execution.
/// </summary>
public sealed class WorkflowExecutionReport
    : AevatarReadModelBase,
      IHasProjectionTimeline,
      IHasProjectionRoleReplies
{
    public string ReportVersion { get; set; } = "1.0";
    public WorkflowExecutionProjectionScope ProjectionScope { get; set; } = WorkflowExecutionProjectionScope.ActorShared;
    public WorkflowExecutionTopologySource TopologySource { get; set; } = WorkflowExecutionTopologySource.RuntimeSnapshot;
    public WorkflowExecutionCompletionStatus CompletionStatus { get; set; } = WorkflowExecutionCompletionStatus.Unknown;
    public string WorkflowName { get; set; } = "";
    public double DurationMs { get; set; }
    public bool? Success { get; set; }
    public string Input { get; set; } = "";
    public string FinalOutput { get; set; } = "";
    public string FinalError { get; set; } = "";
    public List<WorkflowExecutionTopologyEdge> Topology { get; set; } = [];
    public List<WorkflowExecutionStepTrace> Steps { get; set; } = [];
    public List<WorkflowExecutionRoleReply> RoleReplies { get; set; } = [];
    public List<WorkflowExecutionTimelineEvent> Timeline { get; set; } = [];
    public WorkflowExecutionSummary Summary { get; set; } = new();

    public void AddTimeline(ProjectionTimelineEvent timelineEvent)
    {
        ArgumentNullException.ThrowIfNull(timelineEvent);

        Timeline.Add(new WorkflowExecutionTimelineEvent
        {
            Timestamp = timelineEvent.Timestamp,
            Stage = timelineEvent.Stage,
            Message = timelineEvent.Message,
            AgentId = timelineEvent.AgentId,
            StepId = timelineEvent.StepId,
            StepType = timelineEvent.StepType,
            EventType = timelineEvent.EventType,
            Data = timelineEvent.Data.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        });
    }

    public void AddRoleReply(ProjectionRoleReply roleReply)
    {
        ArgumentNullException.ThrowIfNull(roleReply);

        RoleReplies.Add(new WorkflowExecutionRoleReply
        {
            Timestamp = roleReply.Timestamp,
            RoleId = roleReply.RoleId,
            SessionId = roleReply.SessionId,
            Content = roleReply.Content,
            ContentLength = roleReply.ContentLength,
        });
    }
}

public sealed class WorkflowExecutionSummary
{
    public int TotalSteps { get; set; }
    public int RequestedSteps { get; set; }
    public int CompletedSteps { get; set; }
    public int RoleReplyCount { get; set; }
    public Dictionary<string, int> StepTypeCounts { get; set; } = [];
}

public sealed class WorkflowExecutionStepTrace
{
    public string StepId { get; set; } = "";
    public string StepType { get; set; } = "";
    public string TargetRole { get; set; } = "";
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool? Success { get; set; }
    public string WorkerId { get; set; } = "";
    public string OutputPreview { get; set; } = "";
    public string Error { get; set; } = "";
    public Dictionary<string, string> RequestParameters { get; set; } = [];
    public Dictionary<string, string> CompletionMetadata { get; set; } = [];
    public double? DurationMs => RequestedAt.HasValue && CompletedAt.HasValue
        ? Math.Max(0, (CompletedAt.Value - RequestedAt.Value).TotalMilliseconds)
        : null;
}

public sealed class WorkflowExecutionRoleReply
{
    public DateTimeOffset Timestamp { get; set; }
    public string RoleId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Content { get; set; } = "";
    public int ContentLength { get; set; }
}

public sealed class WorkflowExecutionTimelineEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string StepType { get; set; } = "";
    public string EventType { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = [];
}

public sealed record WorkflowExecutionTopologyEdge(string Parent, string Child);
