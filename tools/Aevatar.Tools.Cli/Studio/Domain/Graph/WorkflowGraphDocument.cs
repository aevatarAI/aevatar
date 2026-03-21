namespace Aevatar.Tools.Cli.Studio.Domain.Graph;

public sealed record WorkflowGraphDocument
{
    public string WorkflowName { get; init; } = string.Empty;

    public List<WorkflowGraphNode> Nodes { get; init; } = [];

    public List<WorkflowGraphEdge> Edges { get; init; } = [];
}

public sealed record WorkflowGraphNode(
    string Id,
    string Type,
    string? TargetRole,
    bool HasChildren,
    bool IsImportOnlyType);

public sealed record WorkflowGraphEdge(
    string Id,
    string Source,
    string Target,
    string Kind,
    string? Label = null);
