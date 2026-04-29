using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class AevatarOAuthClientDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<AevatarOAuthClientDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "aevatar-oauth-clients",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
