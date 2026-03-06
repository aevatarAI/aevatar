namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

public interface IRuntimeCallbackSchedulerGrain : IGrainWithStringKey
{
    Task<long> ScheduleTimeoutAsync(
        string callbackId,
        byte[] envelopeBytes,
        int dueTimeMs);

    Task<long> ScheduleTimerAsync(
        string callbackId,
        byte[] envelopeBytes,
        int dueTimeMs,
        int periodMs);

    Task CancelAsync(string callbackId, long expectedGeneration = 0);
}
