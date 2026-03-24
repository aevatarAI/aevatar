namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IConnectorCatalogImportParser
{
    Task<IReadOnlyList<StoredConnectorDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default);
}
