using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Scheduled;

public sealed class UserAgentCatalogNyxCredentialDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<UserAgentCatalogNyxCredentialDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "user-agent-catalog-nyx-credentials",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
