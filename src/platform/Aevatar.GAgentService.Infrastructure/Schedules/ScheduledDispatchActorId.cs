namespace Aevatar.GAgentService.Infrastructure.Schedules;

public static class ScheduledDispatchActorId
{
    private const string Prefix = "scheduled-dispatch:";
    private const string ScheduleIdAllowedCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-";

    public static string Format(string scheduleId)
    {
        var normalized = string.IsNullOrWhiteSpace(scheduleId)
            ? throw new ArgumentException("Schedule id is required.", nameof(scheduleId))
            : scheduleId.Trim();

        if (normalized.Any(static ch => ScheduleIdAllowedCharacters.IndexOf(ch) < 0))
            throw new ArgumentException("Schedule id may only contain letters, digits, '.', '_', and '-'.", nameof(scheduleId));

        return $"{Prefix}{normalized}";
    }

    public static string Unformat(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return string.Empty;

        var normalized = actorId.Trim();
        return normalized.StartsWith(Prefix, StringComparison.Ordinal)
            ? normalized[Prefix.Length..]
            : normalized;
    }
}
