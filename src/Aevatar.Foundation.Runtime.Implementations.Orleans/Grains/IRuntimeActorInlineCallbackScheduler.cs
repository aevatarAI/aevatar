using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Orleans runtime inline callback scheduler bound to the current RuntimeActorGrain turn.
/// Used to avoid an extra grain hop when scheduling callbacks for the current actor.
/// </summary>
public interface IRuntimeActorInlineCallbackScheduler
{
    string ActorId { get; }

    Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        TimeSpan dueTime,
        CancellationToken ct = default);

    Task<RuntimeCallbackLease> ScheduleTimerAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        TimeSpan dueTime,
        TimeSpan period,
        CancellationToken ct = default);

    Task CancelAsync(
        string callbackId,
        long? expectedGeneration = null,
        CancellationToken ct = default);
}
