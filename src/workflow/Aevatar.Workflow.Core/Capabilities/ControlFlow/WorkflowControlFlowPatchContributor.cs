using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowControlFlowPatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingTimeouts,
            next.PendingTimeouts,
            value => patch.PendingTimeouts = value,
            CreatePendingTimeoutFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingRetryBackoffs,
            next.PendingRetryBackoffs,
            value => patch.PendingRetryBackoffs = value,
            CreatePendingRetryBackoffFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingDelays,
            next.PendingDelays,
            value => patch.PendingDelays = value,
            CreatePendingDelayFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingSignalWaits,
            next.PendingSignalWaits,
            value => patch.PendingSignalWaits = value,
            CreatePendingSignalWaitFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingRaceSteps,
            next.PendingRaceSteps,
            value => patch.PendingRaceSteps = value,
            CreateRaceFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingWhileSteps,
            next.PendingWhileSteps,
            value => patch.PendingWhileSteps = value,
            CreateWhileFacts);
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingTimeouts, patch.PendingTimeouts?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingRetryBackoffs, patch.PendingRetryBackoffs?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingDelays, patch.PendingDelays?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingSignalWaits, patch.PendingSignalWaits?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingRaceSteps, patch.PendingRaceSteps?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingWhileSteps, patch.PendingWhileSteps?.Entries);
    }

    private static WorkflowRunPendingTimeoutFacts CreatePendingTimeoutFacts(MapField<string, WorkflowPendingTimeoutState> source)
    {
        var facts = new WorkflowRunPendingTimeoutFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingRetryBackoffFacts CreatePendingRetryBackoffFacts(MapField<string, WorkflowPendingRetryBackoffState> source)
    {
        var facts = new WorkflowRunPendingRetryBackoffFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingDelayFacts CreatePendingDelayFacts(MapField<string, WorkflowPendingDelayState> source)
    {
        var facts = new WorkflowRunPendingDelayFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingSignalWaitFacts CreatePendingSignalWaitFacts(MapField<string, WorkflowPendingSignalWaitState> source)
    {
        var facts = new WorkflowRunPendingSignalWaitFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunRaceFacts CreateRaceFacts(MapField<string, WorkflowRaceState> source)
    {
        var facts = new WorkflowRunRaceFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunWhileFacts CreateWhileFacts(MapField<string, WorkflowWhileState> source)
    {
        var facts = new WorkflowRunWhileFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }
}
