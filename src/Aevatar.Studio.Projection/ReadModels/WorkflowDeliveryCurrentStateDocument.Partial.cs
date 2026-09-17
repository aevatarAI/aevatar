using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Studio.Projection.ReadModels;

public sealed partial class WorkflowDeliveryCurrentStateDocument
    : IProjectionReadModel<WorkflowDeliveryCurrentStateDocument>
{
    string IProjectionReadModel.ActorId => ActorId;

    long IProjectionReadModel.StateVersion => StateVersion;

    string IProjectionReadModel.LastEventId => LastEventId;

    DateTimeOffset IProjectionReadModel.UpdatedAt =>
        UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
}
