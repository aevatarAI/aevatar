using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public sealed partial class StreamingProxyRoomCurrentStateDocument
    : IProjectionReadModel<StreamingProxyRoomCurrentStateDocument>
{
    string IProjectionReadModel.ActorId => ActorId;

    long IProjectionReadModel.StateVersion => StateVersion;

    string IProjectionReadModel.LastEventId => LastEventId;

    DateTimeOffset IProjectionReadModel.UpdatedAt
    {
        get => UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
    }
}
