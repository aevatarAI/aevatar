using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowFanOutPatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingParallelSteps,
            next.PendingParallelSteps,
            value => patch.PendingParallelSteps = value,
            CreateParallelFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingForeachSteps,
            next.PendingForeachSteps,
            value => patch.PendingForeachSteps = value,
            CreateForEachFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingMapReduceSteps,
            next.PendingMapReduceSteps,
            value => patch.PendingMapReduceSteps = value,
            CreateMapReduceFacts);
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingParallelSteps, patch.PendingParallelSteps?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingForeachSteps, patch.PendingForeachSteps?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingMapReduceSteps, patch.PendingMapReduceSteps?.Entries);
    }

    private static WorkflowRunParallelFacts CreateParallelFacts(MapField<string, WorkflowParallelState> source)
    {
        var facts = new WorkflowRunParallelFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunForEachFacts CreateForEachFacts(MapField<string, WorkflowForEachState> source)
    {
        var facts = new WorkflowRunForEachFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunMapReduceFacts CreateMapReduceFacts(MapField<string, WorkflowMapReduceState> source)
    {
        var facts = new WorkflowRunMapReduceFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }
}
