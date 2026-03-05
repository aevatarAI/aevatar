namespace Aevatar.Foundation.Abstractions.Runtime.Callbacks;

public interface IActorRuntimeCallbackScheduler
{
    Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
        RuntimeCallbackTimeoutRequest request,
        CancellationToken ct = default);

    Task<RuntimeCallbackLease> ScheduleTimerAsync(
        RuntimeCallbackTimerRequest request,
        CancellationToken ct = default);

    Task CancelAsync(
        RuntimeCallbackLease lease,
        CancellationToken ct = default);
}
