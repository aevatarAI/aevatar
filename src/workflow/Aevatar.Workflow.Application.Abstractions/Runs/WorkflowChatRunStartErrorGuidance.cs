namespace Aevatar.Workflow.Application.Abstractions.Runs;

public static class WorkflowChatRunStartErrorGuidance
{
    public const string WorkflowNotFound =
        "Workflow is not in the current scope catalog. For Studio/current-scope workflows, list the current scope workflows, choose one runnable descriptor, and retry with its exact workflow_id; when the descriptor includes a definition actor, pass it as actor_id instead of reusing stale opaque ids as workflow_id. If it is published as a NyxID downstream service, call list_external_workflow_capabilities, choose one structured descriptor, and copy its exact typed selector before invoking it. If it is a skill-provided workflow, call use_skill first, then retry with workflow_id.";
}
