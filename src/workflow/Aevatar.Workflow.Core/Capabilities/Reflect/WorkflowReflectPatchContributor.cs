using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowReflectPatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingReflections,
            next.PendingReflections,
            value => patch.PendingReflections = value,
            CreatePendingReflectFacts);
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingReflections, patch.PendingReflections?.Entries);
    }

    private static WorkflowRunPendingReflectFacts CreatePendingReflectFacts(MapField<string, WorkflowPendingReflectState> source)
    {
        var facts = new WorkflowRunPendingReflectFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }
}
