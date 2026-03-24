namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IRoleCatalogImportParser
{
    Task<IReadOnlyList<StoredRoleDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default);
}
