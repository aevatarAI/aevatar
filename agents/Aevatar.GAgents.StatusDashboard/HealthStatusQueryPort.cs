using Aevatar.GAgents.StatusDashboard.Configuration;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.StatusDashboard;

public sealed class HealthStatusQueryPort : IHealthStatusQueryPort
{
    private readonly IHealthProbeOperationalSnapshotStore _store;
    private readonly IReadOnlyList<string> _manifestSlugs;
    private readonly HashSet<string> _manifestSlugSet;

    public HealthStatusQueryPort(
        IHealthProbeOperationalSnapshotStore store,
        IOptions<StatusDashboardOptions> options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(options);
        _manifestSlugs = StatusDashboardManifest
            .FromOptions(options.Value ?? new StatusDashboardOptions())
            .Descriptors
            .Select(static descriptor => descriptor.Slug)
            .Where(static slug => !RetiredStatusProbeTargets.Contains(slug))
            .ToArray();
        _manifestSlugSet = _manifestSlugs.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<HealthProbeOperationalSnapshot>> ListAllAsync(CancellationToken ct = default)
    {
        var results = new List<HealthProbeOperationalSnapshot>(_manifestSlugs.Count);
        foreach (var slug in _manifestSlugs)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await _store.GetAsync(slug, ct);
            if (snapshot is not null && IsInCurrentManifest(snapshot))
                results.Add(snapshot);
        }

        return results;
    }

    public async Task<HealthProbeOperationalSnapshot?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var trimmedSlug = slug?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedSlug)) return null;
        if (!_manifestSlugSet.Contains(trimmedSlug)) return null;
        return await _store.GetAsync(trimmedSlug, ct);
    }

    private bool IsInCurrentManifest(HealthProbeOperationalSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.Target?.Slug) &&
        _manifestSlugSet.Contains(snapshot.Target.Slug);
}
