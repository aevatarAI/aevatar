using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed partial class WorkflowAgentState : ISchedulable
{
    /// <inheritdoc />
    ScheduleState ISchedulable.Schedule => new()
    {
        Enabled = Enabled,
        Cron = ScheduleCron ?? string.Empty,
        Timezone = ScheduleTimezone ?? string.Empty,
        NextRunAt = NextRunAt,
        LastRunAt = LastRunAt,
        ErrorCount = ErrorCount,
    };
}
