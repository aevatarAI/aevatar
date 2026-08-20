namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionOwnedGraph
{
    public required string ProjectionKind { get; init; }

    public required long StateVersion { get; init; }

    public string Scope { get; init; } = string.Empty;

    public string OwnerId { get; init; } = string.Empty;

    public IReadOnlyList<ProjectionGraphNode> Nodes { get; init; } = [];

    public IReadOnlyList<ProjectionGraphEdge> Edges { get; init; } = [];
}
