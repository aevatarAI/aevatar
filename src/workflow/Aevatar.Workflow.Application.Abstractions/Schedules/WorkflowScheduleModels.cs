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
    string? ActorId = null);

public sealed record ScheduledDispatchConfiguration(
    string ScheduleId,
    string DisplayName,
    string TargetActorId,
    EventEnvelope TriggerEnvelope,
    string CronExpression,
    string Timezone,
    bool Enabled,
    IReadOnlyDictionary<string, string> Headers,
    string PayloadTypeUrl);

public sealed record ScheduledDispatchPreparation(
    string TargetActorId,
    EventEnvelope TriggerEnvelope,
    string PayloadTypeUrl);

public static class WorkflowScheduleAdapterHeaderKeys
{
    public const string WorkflowName = "workflow.schedule.workflow_name";
    public const string Prompt = "workflow.schedule.prompt";
    public const string ScopeId = "workflow.schedule.scope_id";
    public const string SourceActorId = "workflow.schedule.source_actor_id";

    public static bool IsAdapterKey(string key) =>
        string.Equals(key, WorkflowName, StringComparison.Ordinal) ||
        string.Equals(key, Prompt, StringComparison.Ordinal) ||
        string.Equals(key, ScopeId, StringComparison.Ordinal) ||
        string.Equals(key, SourceActorId, StringComparison.Ordinal);
}

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
    string ActorId);

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
    string ActorId,
    bool Accepted);

public sealed record WorkflowScheduleRunNowReceipt(
    string ScheduleId,
    string ActorId,
    DateTimeOffset ScheduledFireAt,
    string IdempotencyKey,
    bool Accepted);

public sealed record WorkflowScheduleListResult(
    IReadOnlyList<WorkflowScheduleSummary> Items,
    string? NextCursor,
    long? TotalCount);

public sealed record WorkflowScheduleValidationResult(
    bool Succeeded,
    string Error)
{
    public static WorkflowScheduleValidationResult Success() => new(true, string.Empty);

    public static WorkflowScheduleValidationResult Failed(string error) =>
        new(false, string.IsNullOrWhiteSpace(error) ? "Schedule is invalid." : error);
}

public interface IWorkflowScheduleActorPort
{
    Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default);

    Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default);

    Task<DispatchAdmission> DispatchConfigureAsync(
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

    Task<DispatchAdmission> DispatchConfigureAsync(
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

public static class WorkflowScheduleCalculator
{
    public const string DefaultTimezone = ScheduledDispatchCalculator.DefaultTimezone;

    public static bool TryGetNextOccurrence(
        string cronExpression,
        string? timeZoneId,
        DateTimeOffset fromUtc,
        out DateTimeOffset nextFireAtUtc,
        out string? error) =>
        ScheduledDispatchCalculator.TryGetNextOccurrence(
            cronExpression,
            timeZoneId,
            fromUtc,
            out nextFireAtUtc,
            out error);

    public static WorkflowScheduleValidationResult Validate(
        string cronExpression,
        string? timezone,
        DateTimeOffset? fromUtc = null)
    {
        var validation = ScheduledDispatchCalculator.Validate(cronExpression, timezone, fromUtc);
        return validation.Succeeded
            ? WorkflowScheduleValidationResult.Success()
            : WorkflowScheduleValidationResult.Failed(validation.Error);
    }

    public static IReadOnlyList<DateTimeOffset> GetNextOccurrences(
        string cronExpression,
        string? timeZoneId,
        DateTimeOffset fromUtc,
        int count) =>
        ScheduledDispatchCalculator.GetNextOccurrences(cronExpression, timeZoneId, fromUtc, count);

    public static bool TryResolveTimeZone(
        string? timeZoneId,
        out TimeZoneInfo timeZone,
        out string? error) =>
        ScheduledDispatchCalculator.TryResolveTimeZone(timeZoneId, out timeZone, out error);

    public static TimeSpan ComputeDueTime(DateTimeOffset nextFireAtUtc, DateTimeOffset nowUtc) =>
        ScheduledDispatchCalculator.ComputeDueTime(nextFireAtUtc, nowUtc);

    public static string NormalizeTimezone(string? timeZoneId) =>
        ScheduledDispatchCalculator.NormalizeTimezone(timeZoneId);

    public static string BuildIdempotencyKey(string scheduleId, DateTimeOffset scheduledFireAtUtc) =>
        ScheduledDispatchCalculator.BuildIdempotencyKey(scheduleId, scheduledFireAtUtc);
}
