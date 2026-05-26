using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Projection;

public sealed class VoicePresenceCapabilityReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<VoicePresenceCapabilityReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "voice-presence-capabilities",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
