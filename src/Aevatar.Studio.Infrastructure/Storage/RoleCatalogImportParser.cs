using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class RoleCatalogImportParser : IRoleCatalogImportParser
{
    public Task<IReadOnlyList<StoredRoleDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        RoleCatalogJsonSerializer.ReadCatalogAsync(stream, cancellationToken);
}
