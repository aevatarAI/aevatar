namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionGraphNode
{
    public string Scope { get; set; } = "";

    public string NodeId { get; set; } = "";

    public string NodeType { get; set; } = "";

    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
