namespace Aevatar.Workflow.Infrastructure.Schedules;

internal static class ScheduledDispatchActorId
{
    public static string Format(string scheduleId)
    {
        var normalized = string.IsNullOrWhiteSpace(scheduleId)
            ? throw new ArgumentException("Schedule id is required.", nameof(scheduleId))
            : scheduleId.Trim();

        if (!normalized.All(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.'))
            throw new ArgumentException("Schedule id may only contain letters, digits, '.', '_', ':', and '-'.", nameof(scheduleId));

        return $"scheduled-dispatch:{normalized}";
    }
}
