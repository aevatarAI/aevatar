using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionScopeWatermarkReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<ProjectionScopeWatermarkReadModel>
{
    // Refactor (iter18/cluster-003):
    //   Old pattern: Projection watermark query port reads IEventStore and rebuilds ProjectionScopeState in the query call.
    //   New principle: Watermark queries read a materialized actor-owned/projection-owned read model, with rebuild only in maintenance paths.
    // refactor helper, no behavior change.
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "projection-scope-watermarks",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
