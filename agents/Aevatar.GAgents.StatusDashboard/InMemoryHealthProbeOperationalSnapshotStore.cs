namespace Aevatar.GAgents.StatusDashboard;

public sealed class InMemoryHealthProbeOperationalSnapshotStore : IHealthProbeOperationalSnapshotStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HealthProbeOperationalSnapshot> _snapshots = new(StringComparer.Ordinal);

    public Task UpsertAsync(HealthProbeOperationalSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Target?.Slug);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
            _snapshots[snapshot.Target.Slug] = snapshot.Clone();
        return Task.CompletedTask;
    }

    public Task<HealthProbeOperationalSnapshot?> GetAsync(string slug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
            return Task.FromResult(_snapshots.GetValueOrDefault(slug)?.Clone());
    }
}
