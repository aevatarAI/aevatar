namespace Aevatar.Workflow.Application.Abstractions.Workflows;

public interface IWorkflowDefinitionLookupService
{
    string? GetYaml(string name);

    IReadOnlyList<string> GetNames();
}

public interface IWorkflowDefinitionCatalog : IWorkflowDefinitionLookupService
{
    void Upsert(string name, string yaml);
}
