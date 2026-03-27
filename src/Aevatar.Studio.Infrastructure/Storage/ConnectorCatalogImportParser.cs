using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ConnectorCatalogImportParser : IConnectorCatalogImportParser
{
    public Task<IReadOnlyList<StoredConnectorDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        ConnectorCatalogJsonSerializer.ReadCatalogAsync(stream, cancellationToken);
}
