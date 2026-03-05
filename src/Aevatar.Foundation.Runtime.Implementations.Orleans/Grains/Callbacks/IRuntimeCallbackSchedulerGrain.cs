namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

public interface IRuntimeCallbackSchedulerGrain : IGrainWithStringKey
{
    Task<long> ScheduleTimeoutAsync(
        string callbackId,
        byte[] envelopeBytes,
        int dueTimeMs,
        RuntimeCallbackDeliveryMode deliveryMode);

    Task<long> ScheduleTimerAsync(
        string callbackId,
        byte[] envelopeBytes,
        int dueTimeMs,
        int periodMs,
        RuntimeCallbackDeliveryMode deliveryMode);

    Task CancelAsync(string callbackId, long expectedGeneration = 0);
}
