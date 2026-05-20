using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChatRouting;

public sealed class ChatRoutePolicyDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ChatRoutePolicyCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "chat-route-policies",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
