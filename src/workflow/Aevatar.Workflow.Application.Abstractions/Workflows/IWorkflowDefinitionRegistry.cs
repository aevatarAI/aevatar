namespace Aevatar.Workflow.Application.Abstractions.Workflows;

public interface IWorkflowDefinitionRegistry
{
    void Register(string name, string yaml);

    string? GetYaml(string name);

    IReadOnlyList<string> GetNames();
}
