using Cronos;

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

    Task DispatchConfigureAsync(
        string actorId,
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default);

    Task DispatchEnableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default);

    Task DispatchDisableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default);

    Task DispatchRunNowAsync(
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
    public const string DefaultTimezone = "UTC";

    public static bool TryGetNextOccurrence(
        string cronExpression,
        string? timeZoneId,
        DateTimeOffset fromUtc,
        out DateTimeOffset nextFireAtUtc,
        out string? error)
    {
        nextFireAtUtc = default;
        error = null;

        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            error = "Cron expression is required.";
            return false;
        }

        if (!TryResolveTimeZone(timeZoneId, out var timeZone, out error))
            return false;

        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(cronExpression.Trim(), CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }

        var nextUtc = expression.GetNextOccurrence(fromUtc.UtcDateTime, timeZone, inclusive: false);
        if (!nextUtc.HasValue)
        {
            error = "Cron expression does not yield a future occurrence.";
            return false;
        }

        nextFireAtUtc = new DateTimeOffset(DateTime.SpecifyKind(nextUtc.Value, DateTimeKind.Utc), TimeSpan.Zero);
        return true;
    }

    public static WorkflowScheduleValidationResult Validate(
        string cronExpression,
        string? timezone,
        DateTimeOffset? fromUtc = null)
    {
        return TryGetNextOccurrence(
                cronExpression,
                timezone,
                fromUtc ?? DateTimeOffset.UtcNow,
                out _,
                out var error)
            ? WorkflowScheduleValidationResult.Success()
            : WorkflowScheduleValidationResult.Failed(error ?? "Schedule is invalid.");
    }

    public static IReadOnlyList<DateTimeOffset> GetNextOccurrences(
        string cronExpression,
        string? timeZoneId,
        DateTimeOffset fromUtc,
        int count)
    {
        var boundedCount = Math.Clamp(count, 1, 100);
        if (!TryResolveTimeZone(timeZoneId, out var timeZone, out var timeZoneError))
            throw new ArgumentException(timeZoneError ?? "Timezone is invalid.", nameof(timeZoneId));

        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(cronExpression.Trim(), CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException(ex.Message, nameof(cronExpression), ex);
        }

        var values = new List<DateTimeOffset>(boundedCount);
        var cursor = fromUtc.UtcDateTime;
        for (var i = 0; i < boundedCount; i++)
        {
            var nextUtc = expression.GetNextOccurrence(cursor, timeZone, inclusive: false);
            if (!nextUtc.HasValue)
                break;

            var next = new DateTimeOffset(DateTime.SpecifyKind(nextUtc.Value, DateTimeKind.Utc), TimeSpan.Zero);
            values.Add(next);
            cursor = next.UtcDateTime;
        }

        return values;
    }

    public static bool TryResolveTimeZone(
        string? timeZoneId,
        out TimeZoneInfo timeZone,
        out string? error)
    {
        error = null;
        var normalized = NormalizeTimezone(timeZoneId);

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(normalized);
            return true;
        }
        catch (TimeZoneNotFoundException ex)
        {
            timeZone = TimeZoneInfo.Utc;
            error = ex.Message;
            return false;
        }
        catch (InvalidTimeZoneException ex)
        {
            timeZone = TimeZoneInfo.Utc;
            error = ex.Message;
            return false;
        }
    }

    public static TimeSpan ComputeDueTime(DateTimeOffset nextFireAtUtc, DateTimeOffset nowUtc)
    {
        var delta = nextFireAtUtc - nowUtc;
        return delta <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delta;
    }

    public static string NormalizeTimezone(string? timeZoneId) =>
        string.IsNullOrWhiteSpace(timeZoneId)
            ? DefaultTimezone
            : timeZoneId.Trim();

    public static string BuildIdempotencyKey(string scheduleId, DateTimeOffset scheduledFireAtUtc) =>
        $"schedule:{scheduleId}:fire:{scheduledFireAtUtc.ToUniversalTime():O}";
}
