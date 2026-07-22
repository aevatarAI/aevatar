namespace Aevatar.AI.ToolProviders.Workflow.Ports;

public interface IWorkflowDefinitionCommandAdapter
{
    Task<IReadOnlyList<WorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken ct = default);
    Task<WorkflowDefinitionSnapshot?> GetDefinitionAsync(string workflowName, CancellationToken ct = default);
    Task<WorkflowDefinitionCommandResult> CreateAsync(string workflowName, string yaml, CancellationToken ct = default);
    Task<WorkflowDefinitionCommandResult> UpdateAsync(string workflowName, string yaml, string expectedRevisionId, CancellationToken ct = default);
}
