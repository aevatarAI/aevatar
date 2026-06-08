using Aevatar.Workflow.Application.Abstractions.Schedules;
using Cronos;

namespace Aevatar.Workflow.Application.Schedules;

internal static class WorkflowScheduleCalculator
{
    public const int MaxPreviewCount = 50;

    public static WorkflowScheduleResult<IReadOnlyList<DateTimeOffset>> GetNextFireTimes(
        string cron,
        string timezone,
        DateTimeOffset fromUtc,
        int count)
    {
        var normalizedCron = NormalizeOptional(cron);
        if (normalizedCron == null)
            return WorkflowScheduleResult<IReadOnlyList<DateTimeOffset>>.Failure(
                WorkflowScheduleErrorCode.InvalidCron,
                "Cron expression is required.");

        if (!TryResolveTimezone(timezone, out var timeZone, out var timezoneError))
            return WorkflowScheduleResult<IReadOnlyList<DateTimeOffset>>.Failure(
                WorkflowScheduleErrorCode.InvalidTimezone,
                timezoneError);

        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(normalizedCron, CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            return WorkflowScheduleResult<IReadOnlyList<DateTimeOffset>>.Failure(
                WorkflowScheduleErrorCode.InvalidCron,
                $"Cron expression is invalid: {ex.Message}");
        }

        var boundedCount = Math.Clamp(count, 1, MaxPreviewCount);
        var results = new List<DateTimeOffset>(boundedCount);
        var cursor = fromUtc.ToUniversalTime();
        for (var i = 0; i < boundedCount; i++)
        {
            var next = expression.GetNextOccurrence(cursor, timeZone, inclusive: false);
            if (next == null)
                break;

            var utc = next.Value.ToUniversalTime();
            results.Add(utc);
            cursor = utc;
        }

        return WorkflowScheduleResult<IReadOnlyList<DateTimeOffset>>.Success(results);
    }

    public static WorkflowScheduleResult<DateTimeOffset?> GetNextFireTime(
        string cron,
        string timezone,
        DateTimeOffset fromUtc)
    {
        var result = GetNextFireTimes(cron, timezone, fromUtc, 1);
        if (!result.Succeeded)
            return WorkflowScheduleResult<DateTimeOffset?>.Failure(result.Error.Code, result.Error.Message);

        var next = result.Value is { Count: > 0 }
            ? result.Value[0]
            : (DateTimeOffset?)null;
        return WorkflowScheduleResult<DateTimeOffset?>.Success(next);
    }

    public static bool TryResolveTimezone(
        string timezone,
        out TimeZoneInfo timeZone,
        out string error)
    {
        var normalized = NormalizeOptional(timezone) ?? "UTC";
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(normalized);
            error = string.Empty;
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
            error = $"Timezone '{normalized}' was not found.";
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
            error = $"Timezone '{normalized}' is invalid.";
            return false;
        }
    }

    public static string BuildIdempotencyKey(
        string scheduleId,
        DateTimeOffset scheduledFireAtUtc) =>
        $"schedule:{scheduleId}:fire:{scheduledFireAtUtc.ToUniversalTime():o}";

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }
}
