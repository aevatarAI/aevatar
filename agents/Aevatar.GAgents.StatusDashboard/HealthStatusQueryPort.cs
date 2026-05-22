using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StatusDashboard;

public sealed class HealthStatusQueryPort : IHealthStatusQueryPort
{
    private readonly IProjectionDocumentReader<HealthProbeTargetDocument, string> _reader;

    public HealthStatusQueryPort(IProjectionDocumentReader<HealthProbeTargetDocument, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<IReadOnlyList<HealthProbeTargetDocument>> ListAllAsync(CancellationToken ct = default)
    {
        var result = await _reader.QueryAsync(
            new ProjectionDocumentQuery { Take = 256 },
            ct);
        return result.Items.ToArray();
    }

    public async Task<HealthProbeTargetDocument?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        return await _reader.GetAsync(slug, ct);
    }
}
