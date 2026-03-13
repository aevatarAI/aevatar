namespace Aevatar.Workflow.Application.Abstractions.Authoring;

public interface IWorkflowRuntimeStatusPort
{
    Task<WorkflowLlmStatus> GetStatusAsync(CancellationToken ct = default);
}
