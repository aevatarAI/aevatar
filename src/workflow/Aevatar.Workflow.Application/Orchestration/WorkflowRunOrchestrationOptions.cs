namespace Aevatar.Workflow.Application.Orchestration;

public sealed class WorkflowRunOrchestrationOptions
{
    public int RunProjectionFinalizeGraceTimeoutMs { get; set; } = 1500;
}
