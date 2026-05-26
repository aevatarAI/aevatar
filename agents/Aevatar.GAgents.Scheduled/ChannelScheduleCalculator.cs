using Cronos;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Cron/timezone helpers shared by channel-triggered scheduled runners (skill runner,
/// workflow agent, agent-builder tooling). Pure and stateless; callers wrap the result
/// with their own lease/persistence bookkeeping.
/// </summary>
public static class ChannelScheduleCalculator
{
    public static bool TryGetNextOccurrence(
        string cronExpression,
        TimeZoneInfo timeZone,
        DateTimeOffset fromUtc,
        out DateTimeOffset nextRunAtUtc,
        out string? error)
    {
        nextRunAtUtc = default;
        error = null;

        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            error = "schedule_cron is required";
            return false;
        }

        ArgumentNullException.ThrowIfNull(timeZone);

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
            error = "schedule_cron does not yield a future occurrence";
            return false;
        }

        nextRunAtUtc = new DateTimeOffset(DateTime.SpecifyKind(nextUtc.Value, DateTimeKind.Utc), TimeSpan.Zero);
        return true;
    }

    public static TimeSpan ComputeDueTime(DateTimeOffset nextRunAtUtc, DateTimeOffset nowUtc)
    {
        var delta = nextRunAtUtc - nowUtc;
        return delta <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delta;
    }
}
