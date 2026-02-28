using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.DynamicRuntime.Projection.ReadModels;

public sealed class DynamicRuntimeImageReadModelMetadataProvider : IProjectionDocumentMetadataProvider<DynamicRuntimeImageReadModel>
{
    public DocumentIndexMetadata Metadata => DynamicRuntimeReadModelMetadata.Create("dynamic_runtime_images");
}

public sealed class DynamicRuntimeStackReadModelMetadataProvider : IProjectionDocumentMetadataProvider<DynamicRuntimeStackReadModel>
{
    public DocumentIndexMetadata Metadata => DynamicRuntimeReadModelMetadata.Create("dynamic_runtime_stacks");
}

public sealed class DynamicRuntimeServiceDefinitionReadModelMetadataProvider : IProjectionDocumentMetadataProvider<DynamicRuntimeServiceDefinitionReadModel>
{
    public DocumentIndexMetadata Metadata => DynamicRuntimeReadModelMetadata.Create("dynamic_runtime_services");
}

public sealed class DynamicRuntimeComposeServiceReadModelMetadataProvider : IProjectionDocumentMetadataProvider<DynamicRuntimeComposeServiceReadModel>
{
    public DocumentIndexMetadata Metadata => DynamicRuntimeReadModelMetadata.Create("dynamic_runtime_compose_services");
}

public sealed class DynamicRuntimeComposeEventReadModelMetadataProvider : IProjectionDocumentMetadataProvider<DynamicRuntimeComposeEventReadModel>
{
    public DocumentIndexMetadata Metadata => DynamicRuntimeReadModelMetadata.Create("dynamic_runtime_compose_events");
}

public sealed class DynamicRuntimeContainerReadModelMetadataProvider : IProjectionDocumentMetadataProvider<DynamicRuntimeContainerReadModel>
{
    public DocumentIndexMetadata Metadata => DynamicRuntimeReadModelMetadata.Create("dynamic_runtime_containers");
}

public sealed class DynamicRuntimeRunReadModelMetadataProvider : IProjectionDocumentMetadataProvider<DynamicRuntimeRunReadModel>
{
    public DocumentIndexMetadata Metadata => DynamicRuntimeReadModelMetadata.Create("dynamic_runtime_runs");
}

public sealed class DynamicRuntimeBuildJobReadModelMetadataProvider : IProjectionDocumentMetadataProvider<DynamicRuntimeBuildJobReadModel>
{
    public DocumentIndexMetadata Metadata => DynamicRuntimeReadModelMetadata.Create("dynamic_runtime_build_jobs");
}

internal static class DynamicRuntimeReadModelMetadata
{
    public static DocumentIndexMetadata Create(string indexName) =>
        new(
            IndexName: indexName,
            Mappings: new Dictionary<string, object?>
            {
                ["dynamic"] = "true",
            },
            Settings: new Dictionary<string, object?>(),
            Aliases: new Dictionary<string, object?>());
}
