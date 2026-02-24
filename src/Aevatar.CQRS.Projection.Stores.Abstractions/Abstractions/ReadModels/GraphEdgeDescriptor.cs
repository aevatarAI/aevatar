namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed record GraphEdgeDescriptor(
    string EdgeId,
    string EdgeType,
    string FromNodeId,
    string ToNodeId,
    IReadOnlyDictionary<string, string> Properties,
    DateTimeOffset UpdatedAt);
