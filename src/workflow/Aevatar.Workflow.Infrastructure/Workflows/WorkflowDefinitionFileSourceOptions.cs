namespace Aevatar.Workflow.Infrastructure.Workflows;

public enum WorkflowDefinitionDuplicatePolicy
{
    Throw = 0,
    Skip = 1,
    Override = 2,
}

public sealed class WorkflowDefinitionFileSourceOptions
{
    public IList<string> WorkflowDirectories { get; } = [];

    public WorkflowDefinitionDuplicatePolicy DuplicatePolicy { get; set; } =
        WorkflowDefinitionDuplicatePolicy.Throw;
}
