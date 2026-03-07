using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowSubWorkflowPatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingSubWorkflows,
            next.PendingSubWorkflows,
            value => patch.PendingSubWorkflows = value,
            CreatePendingSubWorkflowFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignRepeatedSliceIfChanged(
            current.ChildActorIds,
            next.ChildActorIds,
            value => patch.ChildActorIds = value);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.SubWorkflowBindings,
            next.SubWorkflowBindings,
            value => patch.SubWorkflowBindings = value,
            CreateSubWorkflowBindingFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingChildRunIdsByParentRunId,
            next.PendingChildRunIdsByParentRunId,
            value => patch.PendingChildRunIdsByParentRunId = value,
            CreatePendingChildRunIdsFacts);
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingSubWorkflows, patch.PendingSubWorkflows?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceRepeatedIfPresent(target.ChildActorIds, patch.ChildActorIds?.ActorIds);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.SubWorkflowBindings, patch.SubWorkflowBindings?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingChildRunIdsByParentRunId, patch.PendingChildRunIdsByParentRunId?.Entries);
    }

    private static WorkflowRunPendingSubWorkflowFacts CreatePendingSubWorkflowFacts(MapField<string, WorkflowPendingSubWorkflowState> source)
    {
        var facts = new WorkflowRunPendingSubWorkflowFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunSubWorkflowBindingFacts CreateSubWorkflowBindingFacts(MapField<string, WorkflowSubWorkflowBindingState> source)
    {
        var facts = new WorkflowRunSubWorkflowBindingFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingChildRunIdsFacts CreatePendingChildRunIdsFacts(MapField<string, WorkflowChildRunIdSet> source)
    {
        var facts = new WorkflowRunPendingChildRunIdsFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }
}
