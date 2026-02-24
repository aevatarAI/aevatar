namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class ProjectionRelationEdge
{
    public string Scope { get; set; } = "";

    public string EdgeId { get; set; } = "";

    public string FromNodeId { get; set; } = "";

    public string ToNodeId { get; set; } = "";

    public string RelationType { get; set; } = "";

    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
