namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed record DocumentIndexMetadata(
    string IndexName,
    IReadOnlyDictionary<string, object?> Mappings,
    IReadOnlyDictionary<string, object?> Settings,
    IReadOnlyDictionary<string, object?> Aliases);
