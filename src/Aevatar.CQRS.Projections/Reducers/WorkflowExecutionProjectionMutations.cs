using Aevatar.CQRS.Projections.Abstractions.ReadModels;

namespace Aevatar.CQRS.Projections.Reducers;

internal static class WorkflowExecutionProjectionMutations
{
    public static WorkflowExecutionStepTrace GetOrCreateStep(WorkflowExecutionReport report, string stepId)
    {
        var step = report.Steps.FirstOrDefault(x => x.StepId == stepId);
        if (step != null) return step;

        step = new WorkflowExecutionStepTrace { StepId = stepId };
        report.Steps.Add(step);
        return step;
    }

    public static void AddTimeline(
        WorkflowExecutionReport report,
        DateTimeOffset timestamp,
        string stage,
        string message,
        string? agentId,
        string? stepId,
        string? stepType,
        string eventType,
        IReadOnlyDictionary<string, string>? data = null)
    {
        report.Timeline.Add(new WorkflowExecutionTimelineEvent
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

    public static void RefreshDerivedFields(WorkflowExecutionReport report)
    {
        report.DurationMs = Math.Max(0, (report.EndedAt - report.StartedAt).TotalMilliseconds);

        var stepTypeCounts = report.Steps
            .Where(x => !string.IsNullOrWhiteSpace(x.StepType))
            .GroupBy(x => x.StepType!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        report.Summary = new WorkflowExecutionSummary
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
