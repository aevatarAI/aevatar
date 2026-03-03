namespace Aevatar.CQRS.Projection.Abstractions;

public sealed record DocumentIndexMetadata(
    string IndexName,
    IReadOnlyDictionary<string, object?> Mappings,
    IReadOnlyDictionary<string, object?> Settings,
    IReadOnlyDictionary<string, object?> Aliases);
