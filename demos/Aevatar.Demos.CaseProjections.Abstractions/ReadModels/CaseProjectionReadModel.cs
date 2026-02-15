namespace Aevatar.Demos.CaseProjections.Abstractions.ReadModels;

public sealed class CaseProjectionReadModel
{
    public string ReadModelVersion { get; set; } = "1.0";
    public string RunId { get; set; } = "";
    public string RootActorId { get; set; } = "";
    public string CaseId { get; set; } = "";
    public string CaseType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Input { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string OwnerId { get; set; } = "";
    public int EscalationLevel { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public string Resolution { get; set; } = "";

    public List<CaseProjectionComment> Comments { get; set; } = [];
    public List<CaseProjectionTimelineItem> Timeline { get; set; } = [];
    public List<CaseTopologyEdge> Topology { get; set; } = [];
    public CaseProjectionSummary Summary { get; set; } = new();
}

public sealed class CaseProjectionComment
{
    public DateTimeOffset Timestamp { get; set; }
    public string AuthorId { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class CaseProjectionTimelineItem
{
    public DateTimeOffset Timestamp { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public string EventType { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CaseProjectionSummary
{
    public int TotalEvents { get; set; }
    public int CommentCount { get; set; }
    public bool IsClosed { get; set; }
    public bool IsEscalated => EscalationLevel > 0;
    public int EscalationLevel { get; set; }
}

public sealed record CaseTopologyEdge(string Parent, string Child);
