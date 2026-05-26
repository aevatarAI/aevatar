using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Studio.Projection.ReadModels;

public sealed partial class UserMemoryCurrentStateDocument
    : IProjectionReadModel<UserMemoryCurrentStateDocument>
{
    string IProjectionReadModel.ActorId => ActorId;

    long IProjectionReadModel.StateVersion => StateVersion;

    string IProjectionReadModel.LastEventId => LastEventId;

    DateTimeOffset IProjectionReadModel.UpdatedAt
    {
        get => UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
    }
}
