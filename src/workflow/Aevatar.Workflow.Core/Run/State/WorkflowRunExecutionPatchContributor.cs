using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunExecutionPatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.Variables,
            next.Variables,
            value => patch.Variables = value,
            CreateVariablesFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.StepExecutions,
            next.StepExecutions,
            value => patch.StepExecutions = value,
            CreateExecutionFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.RetryAttemptsByStepId,
            next.RetryAttemptsByStepId,
            value => patch.RetryAttemptsByStepId = value,
            CreateRetryFacts);
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.Variables, patch.Variables?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.StepExecutions, patch.StepExecutions?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.RetryAttemptsByStepId, patch.RetryAttemptsByStepId?.Entries);
    }

    private static WorkflowRunVariablesFacts CreateVariablesFacts(MapField<string, string> source)
    {
        var facts = new WorkflowRunVariablesFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunStepExecutionFacts CreateExecutionFacts(MapField<string, WorkflowStepExecutionState> source)
    {
        var facts = new WorkflowRunStepExecutionFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunRetryAttemptsFacts CreateRetryFacts(MapField<string, int> source)
    {
        var facts = new WorkflowRunRetryAttemptsFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }
}
