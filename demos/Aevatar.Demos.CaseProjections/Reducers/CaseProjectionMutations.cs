namespace Aevatar.Demos.CaseProjections.Reducers;

internal static class CaseProjectionMutations
{
    public static void AddTimeline(
        CaseProjectionReadModel report,
        DateTimeOffset now,
        string stage,
        string message,
        string eventType,
        IReadOnlyDictionary<string, string>? data = null)
    {
        report.Timeline.Add(new CaseProjectionTimelineItem
        {
            Timestamp = now,
            Stage = stage,
            Message = message,
            EventType = eventType,
            Data = data?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal),
        });
    }

    public static void RefreshDerivedFields(CaseProjectionReadModel report)
    {
        report.Summary = new CaseProjectionSummary
        {
            TotalEvents = report.Timeline.Count,
            CommentCount = report.Comments.Count,
            IsClosed = string.Equals(report.Status, "closed", StringComparison.Ordinal),
            EscalationLevel = report.EscalationLevel,
        };
    }
}
