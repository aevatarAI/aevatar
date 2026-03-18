using Aevatar.Tools.Cli.Studio.Application.Abstractions;

namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

internal sealed class ConnectorCatalogImportParser : IConnectorCatalogImportParser
{
    public Task<IReadOnlyList<StoredConnectorDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        ConnectorCatalogJsonSerializer.ReadCatalogAsync(stream, cancellationToken);
}
