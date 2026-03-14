using Aevatar.Foundation.Abstractions.Runtime.Callbacks;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

public interface IRuntimeCallbackSchedulerGrain : IGrainWithStringKey
{
    Task<long> ScheduleTimeoutAsync(
        string callbackId,
        byte[] envelopeBytes,
        int dueTimeMs,
        RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent);

    Task<long> ScheduleTimerAsync(
        string callbackId,
        byte[] envelopeBytes,
        int dueTimeMs,
        int periodMs,
        RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent);

    Task CancelAsync(string callbackId, long expectedGeneration = 0);

    Task PurgeAsync();
}
