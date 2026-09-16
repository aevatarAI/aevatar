using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Foundation.Projection.Runtime;

public sealed class RuntimeFleetCapabilityAuthorityCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<RuntimeFleetCapabilityAuthorityCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "runtime-fleet-capability-authority-current-states",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
