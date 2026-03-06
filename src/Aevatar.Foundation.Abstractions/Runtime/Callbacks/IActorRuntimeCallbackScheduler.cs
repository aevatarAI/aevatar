namespace Aevatar.Foundation.Abstractions.Runtime.Callbacks;

/// <summary>
/// Public runtime callback scheduler contract.
/// Semantics are durable-only: callbacks must remain schedulable outside the current actor/grain turn.
/// </summary>
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
