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

    public bool SkipSourceCredentialRequiredDefinitionsOnStartup { get; set; }

    public TimeSpan BindCommitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public int BindCommitMaxAttempts { get; set; } = 6;

    public TimeSpan BindCommitRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
