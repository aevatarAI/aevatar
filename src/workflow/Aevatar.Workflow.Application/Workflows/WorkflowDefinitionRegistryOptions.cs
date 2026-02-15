namespace Aevatar.Workflow.Application.Workflows;

public sealed class WorkflowDefinitionRegistryOptions
{
    public IList<string> WorkflowDirectories { get; } = [];

    public bool RegisterBuiltInDirectWorkflow { get; set; } = true;
}
