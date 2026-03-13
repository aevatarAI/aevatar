namespace Aevatar.Workflow.Application.Abstractions.Authoring;

public interface IWorkflowAuthoringCommandApplicationService
{
    Task<PlaygroundWorkflowSaveResult> SaveWorkflowAsync(
        PlaygroundWorkflowSaveRequest request,
        CancellationToken ct = default);
}
