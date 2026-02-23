namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Runtime-managed scheduler for event-store compaction.
/// Event sourcing behavior only reports compaction intents; execution timing is controlled by runtime.
/// </summary>
public interface IEventStoreCompactionScheduler
{
    /// <summary>
    /// Registers a compaction intent for one agent stream.
    /// Implementations should coalesce repeated requests by keeping the maximum target version.
    /// </summary>
    Task ScheduleAsync(string agentId, long compactToVersion, CancellationToken ct = default);

    /// <summary>
    /// Executes queued compaction for the specified agent in an idle lifecycle window.
    /// </summary>
    Task RunOnIdleAsync(string agentId, CancellationToken ct = default);
}
