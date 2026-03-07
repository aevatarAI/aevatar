namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunLifecyclePatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        if (!string.Equals(current.RunId, next.RunId, StringComparison.Ordinal) ||
            !string.Equals(current.Status, next.Status, StringComparison.Ordinal) ||
            !string.Equals(current.ActiveStepId, next.ActiveStepId, StringComparison.Ordinal) ||
            !string.Equals(current.FinalOutput, next.FinalOutput, StringComparison.Ordinal) ||
            !string.Equals(current.FinalError, next.FinalError, StringComparison.Ordinal))
        {
            patch.Lifecycle = new WorkflowRunLifecycleFacts
            {
                RunId = next.RunId ?? string.Empty,
                Status = next.Status ?? string.Empty,
                ActiveStepId = next.ActiveStepId ?? string.Empty,
                FinalOutput = next.FinalOutput ?? string.Empty,
                FinalError = next.FinalError ?? string.Empty,
            };
            changed = true;
        }
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        if (patch.Lifecycle == null)
            return;

        target.RunId = patch.Lifecycle.RunId;
        target.Status = patch.Lifecycle.Status;
        target.ActiveStepId = patch.Lifecycle.ActiveStepId;
        target.FinalOutput = patch.Lifecycle.FinalOutput;
        target.FinalError = patch.Lifecycle.FinalError;
    }
}
