namespace Aevatar.Tools.Cli.Studio.Domain.Models;

public sealed record WorkflowBundle
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string EntryWorkflowName { get; init; } = string.Empty;

    public List<WorkflowDocument> Workflows { get; init; } = [];

    public WorkflowLayoutDocument Layout { get; init; } = new();

    public List<string> Tags { get; init; } = [];

    public List<WorkflowVersion> Versions { get; init; } = [];

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public WorkflowDocument? GetWorkflow(string workflowName) =>
        Workflows.FirstOrDefault(workflow => string.Equals(workflow.Name, workflowName, StringComparison.OrdinalIgnoreCase));

    public IReadOnlySet<string> GetWorkflowNames() =>
        Workflows.Select(workflow => workflow.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
