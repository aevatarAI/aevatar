using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowLlmCallPatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingLlmCalls,
            next.PendingLlmCalls,
            value => patch.PendingLlmCalls = value,
            CreatePendingLlmCallFacts);
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingLlmCalls, patch.PendingLlmCalls?.Entries);
    }

    private static WorkflowRunPendingLlmCallFacts CreatePendingLlmCallFacts(MapField<string, WorkflowPendingLlmCallState> source)
    {
        var facts = new WorkflowRunPendingLlmCallFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }
}
