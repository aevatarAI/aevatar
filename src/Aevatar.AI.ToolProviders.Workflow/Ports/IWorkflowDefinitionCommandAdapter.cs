namespace Aevatar.AI.ToolProviders.Workflow.Ports;

public interface IWorkflowDefinitionCommandAdapter
{
    Task<WorkflowDefinitionCommandResult> CreateAsync(string workflowName, string yaml, CancellationToken ct = default);
    Task<WorkflowDefinitionCommandResult> UpdateAsync(string workflowName, string yaml, string expectedRevisionId, CancellationToken ct = default);
}
