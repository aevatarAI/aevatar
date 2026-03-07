using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowEvaluatePatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingEvaluations,
            next.PendingEvaluations,
            value => patch.PendingEvaluations = value,
            CreatePendingEvaluateFacts);
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingEvaluations, patch.PendingEvaluations?.Entries);
    }

    private static WorkflowRunPendingEvaluateFacts CreatePendingEvaluateFacts(MapField<string, WorkflowPendingEvaluateState> source)
    {
        var facts = new WorkflowRunPendingEvaluateFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }
}
