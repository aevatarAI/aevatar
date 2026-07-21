namespace Aevatar.Workflow.Application.Abstractions.Runs;

public static class WorkflowChatRunStartErrorGuidance
{
    public const string WorkflowNotFound =
        "Workflow is not in the current scope catalog. If it is published as a NyxID downstream service, call nyxid_proxy without a slug or path first to discover available services, then call the exact service_id + slug + path from the same discovered entry. If it is a skill-provided workflow, call use_skill first, then retry with workflow_id.";
}
