using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Reducers;

internal static class ChatRunProjectionMutations
{
    public static ChatStepTrace GetOrCreateStep(ChatRunReport report, string stepId)
    {
        var step = report.Steps.FirstOrDefault(x => x.StepId == stepId);
        if (step != null) return step;

        step = new ChatStepTrace { StepId = stepId };
        report.Steps.Add(step);
        return step;
    }

    public static void AddTimeline(
        ChatRunReport report,
        DateTimeOffset timestamp,
        string stage,
        string message,
        string? agentId,
        string? stepId,
        string? stepType,
        string eventType,
        IReadOnlyDictionary<string, string>? data = null)
    {
        report.Timeline.Add(new ChatTimelineEvent
        {
            Timestamp = timestamp,
            Stage = stage,
            Message = message,
            AgentId = agentId ?? "",
            StepId = stepId ?? "",
            StepType = stepType ?? "",
            EventType = eventType,
            Data = data?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal) ?? [],
        });
    }

    public static void RefreshDerivedFields(ChatRunReport report)
    {
        report.DurationMs = Math.Max(0, (report.EndedAt - report.StartedAt).TotalMilliseconds);

        var stepTypeCounts = report.Steps
            .Where(x => !string.IsNullOrWhiteSpace(x.StepType))
            .GroupBy(x => x.StepType!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        report.Summary = new ChatRunSummary
        {
            TotalSteps = report.Steps.Count,
            RequestedSteps = report.Steps.Count(x => x.RequestedAt != null),
            CompletedSteps = report.Steps.Count(x => x.CompletedAt != null),
            RoleReplyCount = report.RoleReplies.Count,
            StepTypeCounts = stepTypeCounts,
        };
    }

    public static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";
}
