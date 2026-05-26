using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public sealed partial class StreamingProxyChatSessionTerminalSnapshot
    : IProjectionReadModel<StreamingProxyChatSessionTerminalSnapshot>
{
    string IProjectionReadModel.ActorId => ActorId;

    long IProjectionReadModel.StateVersion => StateVersion;

    string IProjectionReadModel.LastEventId => LastEventId;

    DateTimeOffset IProjectionReadModel.UpdatedAt =>
        UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
}
