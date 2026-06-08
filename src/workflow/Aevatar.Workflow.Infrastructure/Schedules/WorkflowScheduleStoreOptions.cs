using Aevatar.Configuration;

namespace Aevatar.Workflow.Infrastructure.Schedules;

public sealed class WorkflowScheduleStoreOptions
{
    public string StorePath { get; set; } =
        Path.Combine(AevatarPaths.Root, "workflow-schedules.pb");

    public bool EnableDispatcher { get; set; } = true;

    public TimeSpan DispatcherPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxDueSchedulesPerTick { get; set; } = 100;
}
