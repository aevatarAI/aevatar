using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Metadata;

public sealed class ServiceCatalogReadModelMetadataProvider : IProjectionDocumentMetadataProvider<ServiceCatalogReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "gagent-service-catalog",
        Mappings: new Dictionary<string, object?>
        {
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["namespace"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "keyword",
                },
            },
        },
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
