namespace Aevatar.Tools.Cli.Studio.Application.Abstractions;

public interface IRoleCatalogImportParser
{
    Task<IReadOnlyList<StoredRoleDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default);
}
