namespace Aevatar.Workflow.Application.Abstractions.Workflows;

public sealed record WorkflowDefinitionRegistration(
    string WorkflowName,
    string WorkflowYaml,
    string DefinitionActorId);

public interface IWorkflowDefinitionRegistry
{
    void Register(string name, string yaml);

    WorkflowDefinitionRegistration? GetDefinition(string name);

    string? GetYaml(string name);

    IReadOnlyList<string> GetNames();
}
