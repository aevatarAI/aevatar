using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Schedules;

public sealed record WorkflowScheduleConfiguration(
    string ScheduleId,
    string DisplayName,
    string WorkflowName,
    string Prompt,
    string CronExpression,
    string Timezone,
    bool Enabled,
    IReadOnlyDictionary<string, string> Headers,
    string? ScopeId = null,
    string? SourceActorId = null);

public sealed record ScheduledDispatchConfiguration(
    string ScheduleId,
    string DisplayName,
    string? TargetActorId,
    EventEnvelope TriggerEnvelope,
    string CronExpression,
    string Timezone,
    bool Enabled,
    IReadOnlyDictionary<string, string> Headers,
    string PayloadTypeUrl,
    WorkflowScheduleTargetDescriptor? WorkflowTarget = null);

public sealed record ScheduledDispatchPreparation(
    string? TargetActorId,
    EventEnvelope TriggerEnvelope,
    string PayloadTypeUrl,
    WorkflowScheduleTargetDescriptor? WorkflowTarget = null);

public sealed record WorkflowScheduleTargetDescriptor(
    string WorkflowName,
    string Prompt,
    string ScopeId,
    string SourceActorId);

public sealed record WorkflowScheduleSummary(
    string ScheduleId,
    string DisplayName,
    string WorkflowName,
    string CronExpression,
    string Timezone,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? LastFireAt,
    string LastRunActorId,
    string LastCommandId,
    string LastCorrelationId,
    string LastError,
    int FireCount,
    int FailureCount,
    IReadOnlyDictionary<string, string> Headers,
    string ScopeId,
    string SourceActorId,
    string ScheduleActorId,
    string TargetActorId);

public sealed record WorkflowScheduleFireRecord(
    DateTimeOffset ScheduledFireAt,
    DateTimeOffset CompletedAt,
    string IdempotencyKey,
    string RunActorId,
    string CommandId,
    string CorrelationId,
    string Error,
    bool Manual);

public sealed record WorkflowScheduleDetail(
    WorkflowScheduleSummary Schedule,
    IReadOnlyList<WorkflowScheduleFireRecord> RecentFires);

public sealed record WorkflowSchedulePreview(
    string CronExpression,
    string Timezone,
    IReadOnlyList<DateTimeOffset> NextFireTimes);

public sealed record WorkflowScheduleMutationReceipt(
    string ScheduleId,
    string ScheduleActorId,
    bool Accepted);

public sealed record WorkflowScheduleRunNowReceipt(
    string ScheduleId,
    string ScheduleActorId,
    DateTimeOffset ScheduledFireAt,
    string IdempotencyKey,
    bool Accepted);

public sealed record WorkflowScheduleListResult(
    IReadOnlyList<WorkflowScheduleSummary> Items,
    string? NextCursor,
    long? TotalCount);

public interface IWorkflowScheduleActorPort
{
    Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default);

    Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default);

    Task<DispatchAdmission> DispatchCreateAsync(
        string actorId,
        WorkflowScheduleConfiguration configuration,
        ScheduledDispatchPreparation dispatch,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchUpdateAsync(
        string actorId,
        WorkflowScheduleConfiguration configuration,
        ScheduledDispatchPreparation dispatch,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchEnableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchDisableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        CancellationToken ct = default);
}

public interface IScheduledDispatchActorPort
{
    Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default);

    Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default);

    Task<DispatchAdmission> DispatchCreateAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchUpdateAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchEnableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchDisableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        CancellationToken ct = default);
}

public interface IWorkflowScheduleQueryPort
{
    Task<WorkflowScheduleDetail?> GetAsync(string scheduleId, CancellationToken ct = default);

    Task<WorkflowScheduleListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default);
}

public interface IWorkflowScheduleApplicationService
{
    Task<WorkflowScheduleMutationReceipt> CreateAsync(
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default);

    Task<WorkflowScheduleMutationReceipt> UpdateAsync(
        string scheduleId,
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default);

    Task<WorkflowScheduleMutationReceipt> EnableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default);

    Task<WorkflowScheduleMutationReceipt> DisableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default);

    Task<WorkflowScheduleDetail?> GetAsync(
        string scheduleId,
        CancellationToken ct = default);

    Task<WorkflowScheduleListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default);

    Task<WorkflowSchedulePreview> PreviewAsync(
        string cronExpression,
        string? timezone,
        int count,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default);

    Task<WorkflowScheduleRunNowReceipt> RunNowAsync(
        string scheduleId,
        CancellationToken ct = default);
}

public abstract class WorkflowScheduleApplicationException : Exception
{
    protected WorkflowScheduleApplicationException(string scheduleId, string message)
        : base(message)
    {
        ScheduleId = scheduleId;
    }

    public string ScheduleId { get; }
}

public sealed class WorkflowScheduleNotFoundException : WorkflowScheduleApplicationException
{
    public WorkflowScheduleNotFoundException(string scheduleId)
        : base(scheduleId, $"Workflow schedule '{scheduleId}' was not found.")
    {
    }
}

public sealed class WorkflowScheduleConflictException : WorkflowScheduleApplicationException
{
    public WorkflowScheduleConflictException(string scheduleId, string message)
        : base(scheduleId, message)
    {
    }
}
