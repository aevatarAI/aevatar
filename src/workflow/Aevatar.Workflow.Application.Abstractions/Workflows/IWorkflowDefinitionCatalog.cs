namespace Aevatar.Workflow.Application.Abstractions.Workflows;

public interface IWorkflowDefinitionLookupService
{
    Task<string?> GetYamlAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct = default);
}

public interface IWorkflowDefinitionCatalog : IWorkflowDefinitionLookupService
{
    Task UpsertAsync(string name, string yaml, CancellationToken ct = default);
}
