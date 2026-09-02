using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Metadata;

public sealed class NyxIdAuthorizationCatalogDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<NyxIdAuthorizationCatalogDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "nyxid-authorization-catalogs",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
