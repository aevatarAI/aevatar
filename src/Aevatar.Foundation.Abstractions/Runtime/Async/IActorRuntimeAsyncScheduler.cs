namespace Aevatar.Foundation.Abstractions.Runtime.Async;

public interface IActorRuntimeAsyncScheduler
{
    Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
        RuntimeTimeoutRequest request,
        CancellationToken ct = default);

    Task<RuntimeCallbackLease> ScheduleTimerAsync(
        RuntimeTimerRequest request,
        CancellationToken ct = default);

    Task CancelAsync(
        string actorId,
        string callbackId,
        long? expectedGeneration = null,
        CancellationToken ct = default);
}
