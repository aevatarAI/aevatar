namespace Aevatar.Workflow.Core;

internal interface IWorkflowRunStatePatchContributor
{
    void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed);

    void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch);
}
