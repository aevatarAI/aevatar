using Orleans.Runtime;
using Orleans.Timers;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

public interface IRuntimeCallbackReminderRegistry
{
    Task RegisterOrUpdateAsync(
        GrainId grainId,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period);

    Task<IReadOnlyList<string>> ListReminderNamesAsync(GrainId grainId);

    Task UnregisterIfExistsAsync(GrainId grainId, string reminderName);
}

internal sealed class OrleansRuntimeCallbackReminderRegistry(
    IReminderRegistry reminderRegistry)
    : IRuntimeCallbackReminderRegistry
{
    private readonly IReminderRegistry _reminderRegistry =
        reminderRegistry ?? throw new ArgumentNullException(nameof(reminderRegistry));

    public async Task RegisterOrUpdateAsync(
        GrainId grainId,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period)
    {
        _ = await _reminderRegistry.RegisterOrUpdateReminder(
                grainId,
                reminderName,
                dueTime,
                period)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListReminderNamesAsync(GrainId grainId)
    {
        var reminders = await _reminderRegistry.GetReminders(grainId).ConfigureAwait(false);
        return reminders.Select(static reminder => reminder.ReminderName).ToArray();
    }

    public async Task UnregisterIfExistsAsync(GrainId grainId, string reminderName)
    {
        var reminder = await _reminderRegistry.GetReminder(grainId, reminderName).ConfigureAwait(false);
        if (reminder != null)
            await _reminderRegistry.UnregisterReminder(grainId, reminder).ConfigureAwait(false);
    }
}
