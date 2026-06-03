using Cronos;

namespace Aevatar.GAgentService.Abstractions.Schedules;

public static class ScheduledDispatchMetadataKeys
{
    public const string ScheduleId = "schedule.id";
    public const string FireAtUtc = "schedule.fire_at_utc";
    public const string IdempotencyKey = "schedule.idempotency_key";
}

public sealed record ScheduledDispatchValidationResult(
    bool Succeeded,
    string Error)
{
    public static ScheduledDispatchValidationResult Success() => new(true, string.Empty);

    public static ScheduledDispatchValidationResult Failed(string error) =>
        new(false, string.IsNullOrWhiteSpace(error) ? "Schedule is invalid." : error);
}

public static class ScheduledDispatchCalculator
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

    public static ScheduledDispatchValidationResult Validate(
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
            ? ScheduledDispatchValidationResult.Success()
            : ScheduledDispatchValidationResult.Failed(error ?? "Schedule is invalid.");
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
