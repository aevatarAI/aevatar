using Aevatar.Tools.Cli.Studio.Application.Abstractions;

namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

internal sealed class RoleCatalogImportParser : IRoleCatalogImportParser
{
    public Task<IReadOnlyList<StoredRoleDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        RoleCatalogJsonSerializer.ReadCatalogAsync(stream, cancellationToken);
}
