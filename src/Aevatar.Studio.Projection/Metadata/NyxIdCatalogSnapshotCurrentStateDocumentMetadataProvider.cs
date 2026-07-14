using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class NyxIdCatalogSnapshotCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<NyxIdCatalogSnapshotCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "studio-nyxid-catalog-snapshots",
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["dynamic"] = true },
        new Dictionary<string, object?>(StringComparer.Ordinal),
        new Dictionary<string, object?>(StringComparer.Ordinal));
}
