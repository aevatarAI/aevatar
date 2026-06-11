using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class AevatarOAuthClientDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<AevatarOAuthClientDocument>
{
    public const string IndexName = "aevatar-oauth-clients";

    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: IndexName,
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
