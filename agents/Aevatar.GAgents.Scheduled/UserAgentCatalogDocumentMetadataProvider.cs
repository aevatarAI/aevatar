using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Scheduled;

public sealed class UserAgentCatalogDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<UserAgentCatalogDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: UserAgentCatalogStorageContracts.ReadModelIndexName,
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
