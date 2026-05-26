using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StatusDashboard.Configuration;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.StatusDashboard;

public sealed class HealthStatusQueryPort : IHealthStatusQueryPort
{
    private readonly IProjectionDocumentReader<HealthProbeTargetDocument, string> _reader;
    private readonly IReadOnlyList<string> _manifestSlugs;
    private readonly HashSet<string> _manifestSlugSet;

    public HealthStatusQueryPort(
        IProjectionDocumentReader<HealthProbeTargetDocument, string> reader,
        IOptions<StatusDashboardOptions> options)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        ArgumentNullException.ThrowIfNull(options);
        _manifestSlugs = StatusDashboardManifest
            .FromOptions(options.Value ?? new StatusDashboardOptions())
            .Descriptors
            .Select(static descriptor => descriptor.Slug)
            .Where(static slug => !RetiredStatusProbeTargets.Contains(slug))
            .ToArray();
        _manifestSlugSet = _manifestSlugs.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<HealthProbeTargetDocument>> ListAllAsync(CancellationToken ct = default)
    {
        var results = new List<HealthProbeTargetDocument>(_manifestSlugs.Count);
        foreach (var slug in _manifestSlugs)
        {
            ct.ThrowIfCancellationRequested();
            var document = await _reader.GetAsync(slug, ct);
            if (document is not null && IsInCurrentManifest(document))
                results.Add(document);
        }

        return results;
    }

    public async Task<HealthProbeTargetDocument?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var trimmedSlug = slug?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedSlug)) return null;
        if (!_manifestSlugSet.Contains(trimmedSlug)) return null;
        return await _reader.GetAsync(trimmedSlug, ct);
    }

    private bool IsInCurrentManifest(HealthProbeTargetDocument document) =>
        !string.IsNullOrWhiteSpace(document.Slug) &&
        _manifestSlugSet.Contains(document.Slug);
}
