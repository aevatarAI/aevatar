using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Abstractions;

public sealed partial class VoicePresenceCapabilityReadModel
    : IProjectionReadModel<VoicePresenceCapabilityReadModel>
{
    string IProjectionReadModel.ActorId => ActorId;

    long IProjectionReadModel.StateVersion => StateVersion;

    string IProjectionReadModel.LastEventId => LastEventId;

    DateTimeOffset IProjectionReadModel.UpdatedAt
    {
        get => UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
    }
}
