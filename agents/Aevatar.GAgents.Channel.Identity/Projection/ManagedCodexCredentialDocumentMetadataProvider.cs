using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class ManagedCodexCredentialDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ManagedCodexCredentialDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "managed-codex-credentials",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
