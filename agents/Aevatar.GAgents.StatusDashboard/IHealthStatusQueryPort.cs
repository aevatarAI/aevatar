namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Application-layer read contract for ephemeral operational health snapshots.
/// </summary>
public interface IHealthStatusQueryPort
{
    Task<IReadOnlyList<HealthProbeOperationalSnapshot>> ListAllAsync(CancellationToken ct = default);

    Task<HealthProbeOperationalSnapshot?> GetBySlugAsync(string slug, CancellationToken ct = default);
}
