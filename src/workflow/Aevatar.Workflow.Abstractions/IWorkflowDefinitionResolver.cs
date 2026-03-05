namespace Aevatar.Workflow.Abstractions;

/// <summary>
/// Resolves workflow YAML by workflow name for runtime invocation.
/// </summary>
public interface IWorkflowDefinitionResolver
{
    Task<string?> GetWorkflowYamlAsync(string workflowName, CancellationToken ct = default);
}

