namespace Aevatar.Tools.Cli.Studio.Application.Abstractions;

public interface IConnectorCatalogImportParser
{
    Task<IReadOnlyList<StoredConnectorDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default);
}
