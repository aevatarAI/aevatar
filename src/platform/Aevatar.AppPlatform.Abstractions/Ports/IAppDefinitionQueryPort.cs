namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppDefinitionQueryPort
{
    Task<IReadOnlyList<AppDefinitionSnapshot>> ListAsync(
        string? ownerScopeId = null,
        CancellationToken ct = default);

    Task<AppDefinitionSnapshot?> GetAsync(
        string appId,
        CancellationToken ct = default);
}
