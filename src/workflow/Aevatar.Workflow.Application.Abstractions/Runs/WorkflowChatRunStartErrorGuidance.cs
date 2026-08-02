namespace Aevatar.Workflow.Application.Abstractions.Runs;

public static class WorkflowChatRunStartErrorGuidance
{
    public const string WorkflowNotFound =
        "Workflow is not in the current scope catalog. For Studio/current-scope workflows, list the current scope workflows, choose one runnable descriptor, and retry with its exact workflow_id. If you intentionally use a definition actor selector, pass actor_id with the descriptor's workflow name, not the stable scope workflow_id. If it is published as a NyxID downstream service, call list_external_workflow_capabilities, choose one structured descriptor, and copy its exact typed selector before invoking it. If it is a skill-provided workflow, call use_skill first, then retry with workflow_id.";
}
