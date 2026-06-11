using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StatusDashboard;

public sealed class HealthProbeTargetDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<HealthProbeTargetDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "health-probe-targets",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
