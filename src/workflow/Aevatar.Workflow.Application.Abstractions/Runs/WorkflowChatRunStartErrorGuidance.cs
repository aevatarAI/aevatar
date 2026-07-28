namespace Aevatar.Workflow.Application.Abstractions.Runs;

public static class WorkflowChatRunStartErrorGuidance
{
    public const string WorkflowNotFound =
        "Workflow is not in the current scope catalog. If it is published as a NyxID downstream service, call list_external_workflow_capabilities, choose one structured descriptor, and copy its exact typed selector before invoking it. If it is a skill-provided workflow, call use_skill first, then retry with workflow_id.";
}
