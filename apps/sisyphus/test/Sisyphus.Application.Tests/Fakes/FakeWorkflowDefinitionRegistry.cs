using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Sisyphus.Application.Tests.Fakes;

internal sealed class FakeWorkflowDefinitionRegistry : IWorkflowDefinitionRegistry
{
    public string? WorkflowYaml { get; set; }

    public void Register(string name, string yaml) { }

    public string? GetYaml(string name) => WorkflowYaml;

    public IReadOnlyList<string> GetNames() => [];
}
