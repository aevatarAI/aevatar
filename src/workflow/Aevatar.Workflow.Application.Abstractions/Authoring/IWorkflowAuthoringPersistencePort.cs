namespace Aevatar.Workflow.Application.Abstractions.Authoring;

public interface IWorkflowAuthoringPersistencePort
{
    Task<PlaygroundWorkflowSaveResult> SaveWorkflowAsync(
        PlaygroundWorkflowSaveRequest request,
        string workflowName,
        CancellationToken ct = default);
}
