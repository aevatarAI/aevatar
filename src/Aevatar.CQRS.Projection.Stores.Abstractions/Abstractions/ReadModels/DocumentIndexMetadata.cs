namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed record DocumentIndexMetadata(
    string IndexName,
    string MappingJson,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyDictionary<string, string> Aliases);
