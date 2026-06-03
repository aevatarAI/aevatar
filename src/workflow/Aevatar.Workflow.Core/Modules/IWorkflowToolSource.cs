namespace Aevatar.Workflow.Core.Modules;

public interface IWorkflowTool
{
    string Name { get; }

    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}

public sealed record WorkflowToolExecutionRequest(
    string ArgumentsJson,
    string RunId,
    string StepId,
    string ExecutionId,
    string CallId,
    string ScopeId,
    string ConnectorHttpAuthorization);

public interface IWorkflowContextualTool : IWorkflowTool
{
    Task<string> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default);
}

public interface IWorkflowToolSource
{
    Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default);
}
