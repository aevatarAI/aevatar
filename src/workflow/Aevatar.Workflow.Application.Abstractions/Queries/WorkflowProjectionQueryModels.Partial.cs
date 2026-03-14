using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Abstractions.Queries;

public sealed partial class WorkflowActorSnapshot
{
    public DateTimeOffset LastUpdatedAt
    {
        get => LastUpdatedAtUtc == null ? default : LastUpdatedAtUtc.ToDateTimeOffset();
        set => LastUpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public bool? LastSuccess
    {
        get => LastSuccessWrapper;
        set => LastSuccessWrapper = value;
    }

    public WorkflowRunCompletionStatus CompletionStatus
    {
        get => (WorkflowRunCompletionStatus)CompletionStatusValue;
        set => CompletionStatusValue = (int)value;
    }
}

public sealed partial class WorkflowActorTimelineItem
{
    public DateTimeOffset Timestamp
    {
        get => TimestampUtc == null ? default : TimestampUtc.ToDateTimeOffset();
        set => TimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class WorkflowActorGraphNode
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc == null ? default : UpdatedAtUtc.ToDateTimeOffset();
        set => UpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class WorkflowActorGraphEdge
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc == null ? default : UpdatedAtUtc.ToDateTimeOffset();
        set => UpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
