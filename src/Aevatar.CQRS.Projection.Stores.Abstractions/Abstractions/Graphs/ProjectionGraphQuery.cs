namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionGraphQuery
{
    public string Scope { get; set; } = "";

    public string RootNodeId { get; set; } = "";

    public ProjectionGraphDirection Direction { get; set; } = ProjectionGraphDirection.Both;

    public IReadOnlyList<string> EdgeTypes { get; set; } = [];

    public int Depth { get; set; } = 1;

    public int Take { get; set; } = 200;
}
