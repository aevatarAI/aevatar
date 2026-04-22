using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelBotDirectCallbackBindingDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ChannelBotDirectCallbackBindingDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "channel-bot-direct-callback-bindings",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
