using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.ReadModels;

public sealed partial class ScopeWorkflowCatalogueRowDocument
    : IProjectionReadModel<ScopeWorkflowCatalogueRowDocument>
{
    string IProjectionReadModel.ActorId => ActorId;

    long IProjectionReadModel.StateVersion => StateVersion;

    string IProjectionReadModel.LastEventId => LastEventId;

    DateTimeOffset IProjectionReadModel.UpdatedAt =>
        UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;

    public DateTimeOffset RowUpdatedAtUtc
    {
        get => RowUpdatedAtUtcValue?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
        set => RowUpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value);
    }

    public DateTimeOffset SourceWatermarkUtc
    {
        get => SourceWatermarkUtcValue?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
        set => SourceWatermarkUtcValue = Timestamp.FromDateTimeOffset(value);
    }
}
