namespace Aevatar.GAgents.StatusDashboard;

public interface IHealthProbeOperationalSnapshotStore
{
    Task UpsertAsync(HealthProbeOperationalSnapshot snapshot, CancellationToken ct = default);

    Task<HealthProbeOperationalSnapshot?> GetAsync(string slug, CancellationToken ct = default);
}
