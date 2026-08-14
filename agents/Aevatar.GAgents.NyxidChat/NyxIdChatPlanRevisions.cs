using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatPlanRevisions
{
    public static void CommitInitial(
        NyxIdChatTaskState task,
        Timestamp committedAt,
        params NyxIdChatTaskStepState[] addedSteps)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(committedAt);
        if (task.PlanRevisions.Count != 0)
            return;

        task.PlanRevision = 1;
        task.PlanRevisionHistoryStart = 1;
        CommitRecord(
            task,
            NyxIdChatPlanRevisionCause.Initial,
            committedAt,
            addedSteps,
            []);
    }

    public static void CommitChange(
        NyxIdChatTaskState task,
        NyxIdChatPlanRevisionCause cause,
        Timestamp committedAt,
        IReadOnlyCollection<NyxIdChatTaskStepState> addedSteps,
        IReadOnlyCollection<NyxIdChatTaskStepState>? cancelledSteps = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(committedAt);
        if (cause is NyxIdChatPlanRevisionCause.Unspecified or NyxIdChatPlanRevisionCause.Initial)
            throw new ArgumentOutOfRangeException(nameof(cause));
        EnsureHistory(task, committedAt, addedSteps);
        task.PlanRevision = checked(task.PlanRevision + 1);
        if (task.PlanRevisionHistoryStart == 0)
            task.PlanRevisionHistoryStart = task.PlanRevision;
        CommitRecord(task, cause, committedAt, addedSteps, cancelledSteps ?? []);
    }

    private static void EnsureHistory(
        NyxIdChatTaskState task,
        Timestamp committedAt,
        IReadOnlyCollection<NyxIdChatTaskStepState> addedSteps)
    {
        if (task.PlanRevisions.Count != 0)
        {
            if (task.PlanRevisionHistoryStart == 0)
                task.PlanRevisionHistoryStart = task.PlanRevisions[0].PlanRevision;
            return;
        }

        task.PlanRevision = Math.Max(1, task.PlanRevision);
        if (task.PlanRevision > 1)
            return;

        task.PlanRevisionHistoryStart = 1;
        var addedStepIds = addedSteps
            .Select(static step => step.StepId)
            .ToHashSet(StringComparer.Ordinal);
        var originalSteps = task.Steps
            .Where(step => !addedStepIds.Contains(step.StepId))
            .ToArray();
        CommitRecord(
            task,
            NyxIdChatPlanRevisionCause.Initial,
            task.CreatedAt?.Clone() ?? committedAt.Clone(),
            originalSteps,
            []);
    }

    private static void CommitRecord(
        NyxIdChatTaskState task,
        NyxIdChatPlanRevisionCause cause,
        Timestamp committedAt,
        IReadOnlyCollection<NyxIdChatTaskStepState> addedSteps,
        IReadOnlyCollection<NyxIdChatTaskStepState> cancelledSteps)
    {
        foreach (var step in addedSteps)
            step.AddedInPlanRevision = task.PlanRevision;
        foreach (var step in cancelledSteps)
            step.CancelledInPlanRevision = task.PlanRevision;

        var record = new NyxIdChatPlanRevisionRecord
        {
            PlanRevision = task.PlanRevision,
            RevisionCause = cause,
            CommittedAt = committedAt.Clone(),
        };
        record.AddedStepIds.AddRange(addedSteps
            .Select(static step => step.StepId)
            .Where(static stepId => !string.IsNullOrWhiteSpace(stepId))
            .Distinct(StringComparer.Ordinal));
        record.CancelledStepIds.AddRange(cancelledSteps
            .Select(static step => step.StepId)
            .Where(static stepId => !string.IsNullOrWhiteSpace(stepId))
            .Distinct(StringComparer.Ordinal));
        task.PlanRevisions.Add(record);
    }
}
