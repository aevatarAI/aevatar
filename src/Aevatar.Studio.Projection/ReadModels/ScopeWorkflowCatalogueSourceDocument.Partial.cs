using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.ReadModels;

public sealed partial class ScopeWorkflowCatalogueSourceDocument
    : IProjectionReadModel<ScopeWorkflowCatalogueSourceDocument>
{
    public const string DraftSourceKind = "draft";
    public const string ServiceSourceKind = "service";
    public const string CommittedSourceKind = ServiceSourceKind;

    string IProjectionReadModel.ActorId => ActorId;

    long IProjectionReadModel.StateVersion => StateVersion;

    string IProjectionReadModel.LastEventId => LastEventId;

    DateTimeOffset IProjectionReadModel.UpdatedAt =>
        UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;

    public DateTimeOffset SourceUpdatedAtUtc
    {
        get => SourceUpdatedAtUtcValue?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
        set => SourceUpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value);
    }
}
