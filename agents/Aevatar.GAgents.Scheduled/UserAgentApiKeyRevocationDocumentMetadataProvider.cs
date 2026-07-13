using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Scheduled;

public sealed class UserAgentApiKeyRevocationDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<UserAgentApiKeyRevocationDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "user-agent-api-key-revocations",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
