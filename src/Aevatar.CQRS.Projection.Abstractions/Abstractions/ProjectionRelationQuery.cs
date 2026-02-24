namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class ProjectionRelationQuery
{
    public string Scope { get; set; } = "";

    public string RootNodeId { get; set; } = "";

    public ProjectionRelationDirection Direction { get; set; } = ProjectionRelationDirection.Both;

    public IReadOnlyList<string> RelationTypes { get; set; } = [];

    public int Depth { get; set; } = 1;

    public int Take { get; set; } = 200;
}
