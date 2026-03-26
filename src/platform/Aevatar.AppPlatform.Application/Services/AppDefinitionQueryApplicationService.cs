using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Access;
using Aevatar.AppPlatform.Abstractions.Ports;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class AppDefinitionQueryApplicationService : IAppDefinitionQueryPort
{
    private readonly IAppRegistryReader _registryReader;

    public AppDefinitionQueryApplicationService(IAppRegistryReader registryReader)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    }

    public Task<IReadOnlyList<AppDefinitionSnapshot>> ListAsync(string? ownerScopeId = null, CancellationToken ct = default) =>
        _registryReader.ListAppsAsync(ownerScopeId, ct);

    public Task<AppDefinitionSnapshot?> GetAsync(string appId, CancellationToken ct = default) =>
        _registryReader.GetAppAsync(appId, ct);

    public static AppAccessResource BuildAccessResource(AppDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AppAccessResource(
            snapshot.OwnerScopeId,
            AppId: snapshot.AppId,
            IsPublic: snapshot.Visibility == AppVisibility.Public);
    }
}
