namespace Aevatar.Workflow.Application.Abstractions.Authoring;

public interface IWorkflowDefinitionSourceRefreshPort
{
    Task RefreshAsync(string? workflowName = null, CancellationToken ct = default);
}
