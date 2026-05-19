using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed partial class ProjectionScopeWatermarkReadModel
    : IProjectionReadModel<ProjectionScopeWatermarkReadModel>
{
    // Refactor (iter18/cluster-003):
    //   Old pattern: Projection watermark query port reads IEventStore and rebuilds ProjectionScopeState in the query call.
    //   New principle: Watermark queries read a materialized actor-owned/projection-owned read model, with rebuild only in maintenance paths.
    // New pattern: query reads materialized watermark; event replay is repair/recovery only.
    public string ActorId => Id;

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
        set => UpdatedAtUtc = Timestamp.FromDateTimeOffset(value);
    }
}
