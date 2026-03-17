namespace Aevatar.Foundation.Projection.ReadModels;

public sealed class ProjectionTimelineEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string StepType { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> Data { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
