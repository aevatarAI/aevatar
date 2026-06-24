using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ChannelBotRegistrationDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "channel-bot-registrations",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
