using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.ReadModels;

public enum WorkflowExecutionBoardCompletionStatus
{
    Running = 0,
    Completed = 1,
    TimedOut = 2,
    Failed = 3,
    Stopped = 4,
    NotFound = 5,
    Disabled = 6,
    WaitingForSignal = 7,
    Unknown = 99,
}

public enum WorkflowExecutionBoardNodeStatus
{
    Unknown = 0,
    Running = 1,
    Waiting = 2,
    Pending = 3,
    Failed = 4,
    Completed = 5,
}

/// <summary>
/// Durable workflow-board artifact document. It implements <see cref="IProjectionReadModel{TSelf}"/>
/// only because projection document stores/readers require that storage envelope; current-state
/// replica semantics are expressed by ICurrentStateProjectionMaterializer implementations, not by
/// this storage contract.
/// </summary>
public sealed partial class WorkflowExecutionBoardDocument
    : IProjectionReadModel<WorkflowExecutionBoardDocument>
{
    public string ActorId => RootActorId;

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public WorkflowExecutionBoardCompletionStatus CompletionStatus
    {
        get => (WorkflowExecutionBoardCompletionStatus)CompletionStatusValue;
        set => CompletionStatusValue = (int)value;
    }

    public DateTimeOffset? LastNodeUpdatedAt
    {
        get => LastNodeUpdatedAtUtcValue == null ? null : LastNodeUpdatedAtUtcValue.ToDateTimeOffset();
        set => LastNodeUpdatedAtUtcValue = value.HasValue
            ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime())
            : null;
    }

    public WorkflowExecutionBoardSummaryReadModel Summary
    {
        get => SummaryValue ??= new WorkflowExecutionBoardSummaryReadModel();
        set => SummaryValue = value ?? new WorkflowExecutionBoardSummaryReadModel();
    }
}

public sealed partial class WorkflowExecutionBoardNodeReadModel
{
    public WorkflowExecutionBoardNodeStatus Status
    {
        get => (WorkflowExecutionBoardNodeStatus)StatusValue;
        set => StatusValue = (int)value;
    }

    public DateTimeOffset? RequestedAt
    {
        get => RequestedAtUtcValue == null ? null : RequestedAtUtcValue.ToDateTimeOffset();
        set => RequestedAtUtcValue = value.HasValue
            ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime())
            : null;
    }

    public DateTimeOffset? CompletedAt
    {
        get => CompletedAtUtcValue == null ? null : CompletedAtUtcValue.ToDateTimeOffset();
        set => CompletedAtUtcValue = value.HasValue
            ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime())
            : null;
    }

    public DateTimeOffset? UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? null : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = value.HasValue
            ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime())
            : null;
    }
}
