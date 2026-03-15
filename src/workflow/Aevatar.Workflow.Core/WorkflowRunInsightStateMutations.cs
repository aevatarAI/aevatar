using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRunInsightStateMutations
{
    public static void EnsureInitialized(
        WorkflowRunInsightState state,
        string rootActorId,
        string workflowName,
        string commandId,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(state.RootActorId))
            state.RootActorId = rootActorId;
        if (string.IsNullOrWhiteSpace(state.WorkflowName) && !string.IsNullOrWhiteSpace(workflowName))
            state.WorkflowName = workflowName;
        if (string.IsNullOrWhiteSpace(state.CommandId) && !string.IsNullOrWhiteSpace(commandId))
            state.CommandId = commandId;
        if (string.IsNullOrWhiteSpace(state.ReportVersion))
            state.ReportVersion = "2.0";
        if (state.CreatedAt == default)
            state.CreatedAt = observedAt;
        if (state.StartedAt == default)
            state.StartedAt = observedAt;
        if (state.EndedAt == default)
            state.EndedAt = observedAt;
        if (state.SummaryValue == null)
            state.SummaryValue = new WorkflowRunInsightSummary();
    }

    public static void RecordObserved(
        WorkflowRunInsightState state,
        string sourceEventId,
        long stateVersion,
        DateTimeOffset observedAt)
    {
        if (stateVersion > 0)
            state.StateVersion = stateVersion;
        if (!string.IsNullOrWhiteSpace(sourceEventId))
            state.LastEventId = sourceEventId;
        state.UpdatedAt = observedAt;
    }

    public static WorkflowRunInsightStepTrace GetOrCreateStep(
        WorkflowRunInsightState state,
        string stepId)
    {
        var step = state.StepEntries.FirstOrDefault(x => x.StepId == stepId);
        if (step != null)
            return step;

        step = new WorkflowRunInsightStepTrace
        {
            StepId = stepId,
        };
        state.StepEntries.Add(step);
        return step;
    }

    public static void AddTimeline(
        WorkflowRunInsightState state,
        DateTimeOffset timestamp,
        string stage,
        string message,
        string? agentId,
        string? stepId,
        string? stepType,
        string eventType,
        IEnumerable<KeyValuePair<string, string>>? data = null)
    {
        state.TimelineEntries.Add(new WorkflowRunInsightTimelineEvent
        {
            Timestamp = timestamp,
            Stage = stage,
            Message = message,
            AgentId = agentId ?? string.Empty,
            StepId = stepId ?? string.Empty,
            StepType = stepType ?? string.Empty,
            EventType = eventType,
            DataMap =
            {
                data?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal) ?? []
            },
        });
    }

    public static void AddRoleReply(
        WorkflowRunInsightState state,
        DateTimeOffset timestamp,
        string roleId,
        string sessionId,
        string content)
    {
        state.RoleReplyEntries.Add(new WorkflowRunInsightRoleReply
        {
            Timestamp = timestamp,
            RoleId = roleId,
            SessionId = sessionId,
            Content = content,
            ContentLength = content.Length,
        });
    }

    public static void ReplaceTopology(
        WorkflowRunInsightState state,
        IEnumerable<WorkflowRunInsightTopologyEdge> edges)
    {
        state.TopologyEntries.Clear();
        state.TopologyEntries.Add(edges);
    }

    public static void RefreshDerivedFields(
        WorkflowRunInsightState state,
        DateTimeOffset observedAt)
    {
        state.UpdatedAt = observedAt;
        if (state.EndedAt < state.StartedAt)
            state.EndedAt = observedAt;

        var summary = state.SummaryValue ?? new WorkflowRunInsightSummary();
        summary.TotalSteps = state.StepEntries.Count;
        summary.RequestedSteps = state.StepEntries.Count(x => x.RequestedAtUtcValue != null);
        summary.CompletedSteps = state.StepEntries.Count(x => x.CompletedAtUtcValue != null);
        summary.RoleReplyCount = state.RoleReplyEntries.Count;
        summary.StepTypeCountsMap.Clear();

        foreach (var group in state.StepEntries
                     .Where(x => !string.IsNullOrWhiteSpace(x.StepType))
                     .GroupBy(x => x.StepType, StringComparer.OrdinalIgnoreCase))
        {
            summary.StepTypeCountsMap[group.Key] = group.Count();
        }

        state.SummaryValue = summary;
    }

    public static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";

    public static void ReplaceMap<TKey, TValue>(
        MapField<TKey, TValue> target,
        IDictionary<TKey, TValue> source)
        where TKey : notnull
    {
        target.Clear();
        target.Add(source);
    }
}
