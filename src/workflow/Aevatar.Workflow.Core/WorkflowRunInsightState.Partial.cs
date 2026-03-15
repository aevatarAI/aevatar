using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunInsightState
{
    public WorkflowRunInsightCompletionStatus CompletionStatus
    {
        get => (WorkflowRunInsightCompletionStatus)CompletionStatusValue;
        set => CompletionStatusValue = (int)value;
    }

    public DateTimeOffset CreatedAt
    {
        get => CreatedAtUtcValue == null ? default : CreatedAtUtcValue.ToDateTimeOffset();
        set => CreatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public DateTimeOffset StartedAt
    {
        get => StartedAtUtcValue == null ? default : StartedAtUtcValue.ToDateTimeOffset();
        set => StartedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public DateTimeOffset EndedAt
    {
        get => EndedAtUtcValue == null ? default : EndedAtUtcValue.ToDateTimeOffset();
        set => EndedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public bool? Success
    {
        get => SuccessWrapper;
        set => SuccessWrapper = value;
    }
}

public sealed partial class WorkflowRunInsightStepTrace
{
    public DateTimeOffset? RequestedAt
    {
        get => RequestedAtUtcValue == null ? null : RequestedAtUtcValue.ToDateTimeOffset();
        set => RequestedAtUtcValue = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }

    public DateTimeOffset? CompletedAt
    {
        get => CompletedAtUtcValue == null ? null : CompletedAtUtcValue.ToDateTimeOffset();
        set => CompletedAtUtcValue = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }

    public bool? Success
    {
        get => SuccessWrapper;
        set => SuccessWrapper = value;
    }
}

public sealed partial class WorkflowRunInsightRoleReply
{
    public DateTimeOffset Timestamp
    {
        get => TimestampUtcValue == null ? default : TimestampUtcValue.ToDateTimeOffset();
        set => TimestampUtcValue = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class WorkflowRunInsightTimelineEvent
{
    public DateTimeOffset Timestamp
    {
        get => TimestampUtcValue == null ? default : TimestampUtcValue.ToDateTimeOffset();
        set => TimestampUtcValue = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
