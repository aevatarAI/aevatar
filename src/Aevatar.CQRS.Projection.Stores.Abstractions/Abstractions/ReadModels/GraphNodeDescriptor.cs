namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed record GraphNodeDescriptor(
    string NodeId,
    string NodeType,
    IReadOnlyDictionary<string, string> Properties,
    DateTimeOffset UpdatedAt);
