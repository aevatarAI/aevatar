namespace Aevatar.Cqrs.Projections.Abstractions.ReadModels;

/// <summary>
/// Read model for one chat workflow run.
/// </summary>
public sealed class ChatRunReport
{
    public string ReportVersion { get; set; } = "1.0";
    public string WorkflowName { get; set; } = "";
    public string RootActorId { get; set; } = "";
    public string RunId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double DurationMs { get; set; }
    public bool? Success { get; set; }
    public string Input { get; set; } = "";
    public string FinalOutput { get; set; } = "";
    public string FinalError { get; set; } = "";
    public List<ChatTopologyEdge> Topology { get; set; } = [];
    public List<ChatStepTrace> Steps { get; set; } = [];
    public List<ChatRoleReply> RoleReplies { get; set; } = [];
    public List<ChatTimelineEvent> Timeline { get; set; } = [];
    public ChatRunSummary Summary { get; set; } = new();
}

public sealed class ChatRunSummary
{
    public int TotalSteps { get; set; }
    public int RequestedSteps { get; set; }
    public int CompletedSteps { get; set; }
    public int RoleReplyCount { get; set; }
    public Dictionary<string, int> StepTypeCounts { get; set; } = [];
}

public sealed class ChatStepTrace
{
    public string StepId { get; set; } = "";
    public string StepType { get; set; } = "";
    public string RunId { get; set; } = "";
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

public sealed class ChatRoleReply
{
    public DateTimeOffset Timestamp { get; set; }
    public string RoleId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Content { get; set; } = "";
    public int ContentLength { get; set; }
}

public sealed class ChatTimelineEvent
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

public sealed record ChatTopologyEdge(string Parent, string Child);
