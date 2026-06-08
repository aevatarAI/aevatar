using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Abstractions.Schedules;

public enum WorkflowScheduleStatus
{
    Disabled = 0,
    Enabled = 1,
}

public enum WorkflowScheduleFireStatus
{
    Accepted = 0,
    Duplicate = 1,
    Rejected = 2,
}

public sealed record WorkflowScheduleTarget(
    string Prompt,
    WorkflowChatSource Source,
    string? SessionId = null,
    IReadOnlyList<WorkflowChatInputPart>? InputParts = null,
    IReadOnlyDictionary<string, string>? Annotations = null,
    string? ScopeId = null,
    IReadOnlyDictionary<string, string>? Headers = null);

public sealed record WorkflowScheduleDefinition(
    string ScheduleId,
    string Name,
    string Cron,
    string Timezone,
    WorkflowScheduleStatus Status,
    WorkflowScheduleTarget Target,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextFireAtUtc,
    WorkflowScheduleWakeupLease? WakeupLease = null);

public sealed record WorkflowScheduleWakeupLease(
    string ActorId,
    string CallbackId,
    long Generation,
    WorkflowScheduleWakeupBackend Backend,
    int SlotEpoch);

public enum WorkflowScheduleWakeupBackend
{
    InMemory = 0,
    Dedicated = 1,
}

public sealed record WorkflowScheduleRunRecord(
    string RunRecordId,
    string ScheduleId,
    DateTimeOffset ScheduledFireAtUtc,
    DateTimeOffset FiredAtUtc,
    string IdempotencyKey,
    WorkflowScheduleFireStatus Status,
    string? AcceptedCommandId = null,
    string? CorrelationId = null,
    string? ActorId = null,
    string? Error = null);

public sealed record WorkflowSchedulePreview(
    string Cron,
    string Timezone,
    DateTimeOffset FromUtc,
    IReadOnlyList<DateTimeOffset> FireTimesUtc);

public sealed record WorkflowScheduleCreateCommand(
    string? ScheduleId,
    string Name,
    string Cron,
    string Timezone,
    WorkflowScheduleTarget Target,
    bool Enabled = true);

public sealed record WorkflowScheduleUpdateCommand(
    string? Name = null,
    string? Cron = null,
    string? Timezone = null,
    WorkflowScheduleTarget? Target = null);

public sealed record WorkflowScheduleListQuery(
    WorkflowScheduleStatus? Status = null,
    int Skip = 0,
    int Take = 100);

public sealed record WorkflowScheduleListResult(
    IReadOnlyList<WorkflowScheduleDefinition> Items,
    int Total);

public sealed record WorkflowScheduleFireRequest(
    string ScheduleId,
    DateTimeOffset? ScheduledFireAtUtc = null,
    bool Force = false,
    bool AdvanceSchedule = false);

public sealed record WorkflowScheduleFireResult(
    WorkflowScheduleFireStatus Status,
    WorkflowScheduleRunRecord Run,
    string? StatusUrl = null);

public enum WorkflowScheduleErrorCode
{
    None = 0,
    InvalidScheduleId = 1,
    InvalidName = 2,
    InvalidCron = 3,
    InvalidTimezone = 4,
    InvalidTarget = 5,
    NotFound = 6,
    AlreadyExists = 7,
    Disabled = 8,
    DispatchRejected = 9,
}

public sealed record WorkflowScheduleError(
    WorkflowScheduleErrorCode Code,
    string Message)
{
    public static WorkflowScheduleError None { get; } = new(WorkflowScheduleErrorCode.None, string.Empty);
}

public sealed record WorkflowScheduleResult<T>(
    T? Value,
    WorkflowScheduleError Error)
{
    public bool Succeeded => Error.Code == WorkflowScheduleErrorCode.None;

    public static WorkflowScheduleResult<T> Success(T value) =>
        new(value, WorkflowScheduleError.None);

    public static WorkflowScheduleResult<T> Failure(WorkflowScheduleErrorCode code, string message) =>
        new(default, new WorkflowScheduleError(code, message));
}

public interface IWorkflowScheduleApplicationService
{
    Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> CreateAsync(
        WorkflowScheduleCreateCommand command,
        CancellationToken ct = default);

    Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> UpdateAsync(
        string scheduleId,
        WorkflowScheduleUpdateCommand command,
        CancellationToken ct = default);

    Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> EnableAsync(
        string scheduleId,
        CancellationToken ct = default);

    Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> DisableAsync(
        string scheduleId,
        CancellationToken ct = default);

    Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> GetAsync(
        string scheduleId,
        CancellationToken ct = default);

    Task<WorkflowScheduleListResult> ListAsync(
        WorkflowScheduleListQuery query,
        CancellationToken ct = default);

    Task<WorkflowScheduleResult<WorkflowSchedulePreview>> PreviewAsync(
        string cron,
        string timezone,
        DateTimeOffset? fromUtc,
        int count,
        CancellationToken ct = default);

    Task<WorkflowScheduleResult<WorkflowScheduleFireResult>> RunNowAsync(
        WorkflowScheduleFireRequest request,
        CancellationToken ct = default);
}
