using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

public interface IRuntimeCallbackSchedulerGrain : IGrainWithStringKey
{
    Task<long> ScheduleTimeoutAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        int dueTimeMs,
        RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent);

    Task<long> ScheduleTimerAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        int dueTimeMs,
        int periodMs,
        RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent);

    Task CancelAsync(
        string callbackId,
        long expectedGeneration = 0,
        int expectedSlotEpoch = RuntimeCallbackSlotEpoch.Unspecified);

    Task PurgeAsync();

    Task<long> EnsureRuntimeFleetReconcileTimerAsync() =>
        Task.FromException<long>(new NotSupportedException(
            "This callback scheduler grain does not implement the protected fleet reconcile slot."));

    Task<bool> VerifyRuntimeFleetReconcileDeliveryAsync(EventEnvelope envelope) =>
        Task.FromResult(false);

    Task AcknowledgeRuntimeFleetReconcileDeliveryAsync(
        string envelopeId,
        long generation,
        long fireIndex,
        int slotEpoch) =>
        Task.FromException(new NotSupportedException(
            "This callback scheduler grain does not implement protected fleet reconcile acknowledgement."));
}
