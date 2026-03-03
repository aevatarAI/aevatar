using Aevatar.Foundation.Core.EventSourcing;

namespace Aevatar.Foundation.Runtime.Actors;

/// <summary>
/// Default deactivation hook: triggers deferred event-store compaction on actor idle.
/// </summary>
public sealed class EventStoreCompactionDeactivationHook : IActorDeactivationHook
{
    private readonly IEventStoreCompactionScheduler _compactionScheduler;

    public EventStoreCompactionDeactivationHook(IEventStoreCompactionScheduler compactionScheduler)
    {
        _compactionScheduler = compactionScheduler;
    }

    public async Task OnDeactivatedAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        await _compactionScheduler.RunOnIdleAsync(actorId, ct);
    }
}
