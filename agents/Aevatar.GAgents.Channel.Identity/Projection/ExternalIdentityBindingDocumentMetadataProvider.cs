using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class ExternalIdentityBindingDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ExternalIdentityBindingDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "external-identity-bindings",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
