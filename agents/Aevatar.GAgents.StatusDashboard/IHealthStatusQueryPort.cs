namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Application-layer read contract for the status dashboard. Implementations
/// must never prime the projection nor read the writeside actor state directly
/// — return what is currently materialized in the projection document store.
/// </summary>
public interface IHealthStatusQueryPort
{
    Task<IReadOnlyList<HealthProbeTargetDocument>> ListAllAsync(CancellationToken ct = default);

    Task<HealthProbeTargetDocument?> GetBySlugAsync(string slug, CancellationToken ct = default);
}
