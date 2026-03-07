using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowHumanInteractionPatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingHumanGates,
            next.PendingHumanGates,
            value => patch.PendingHumanGates = value,
            CreatePendingHumanGateFacts);
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingHumanGates, patch.PendingHumanGates?.Entries);
    }

    private static WorkflowRunPendingHumanGateFacts CreatePendingHumanGateFacts(MapField<string, WorkflowPendingHumanGateState> source)
    {
        var facts = new WorkflowRunPendingHumanGateFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }
}
