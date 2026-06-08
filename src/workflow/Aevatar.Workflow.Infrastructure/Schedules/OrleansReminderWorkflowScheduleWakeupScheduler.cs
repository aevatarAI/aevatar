using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Schedules;
using Aevatar.Workflow.Core.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.Schedules;

public sealed class OrleansReminderWorkflowScheduleWakeupScheduler : IWorkflowScheduleWakeupScheduler
{
    private const string CallbackIdPrefix = "workflow-schedule-due";
    private const string ActorIdPrefix = "workflow-schedule-wakeup";

    private readonly IActorRuntimeCallbackScheduler _callbacks;
    private readonly IActorRuntime _runtime;
    private readonly IWorkflowScheduleStore _store;
    private readonly TimeProvider _clock;

    public OrleansReminderWorkflowScheduleWakeupScheduler(
        IActorRuntimeCallbackScheduler callbacks,
        IActorRuntime runtime,
        IWorkflowScheduleStore store,
        TimeProvider? clock = null)
    {
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task ScheduleAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ct.ThrowIfCancellationRequested();

        if (definition.Status != WorkflowScheduleStatus.Enabled || definition.NextFireAtUtc == null)
        {
            await CancelAsync(definition.ScheduleId, ct);
            return;
        }

        await CancelLeaseAsync(definition.WakeupLease, ct);

        var fireAtUtc = definition.NextFireAtUtc.Value.ToUniversalTime();
        var dueTime = fireAtUtc - _clock.GetUtcNow().ToUniversalTime();
        if (dueTime <= TimeSpan.Zero)
            dueTime = TimeSpan.FromMilliseconds(1);

        var callbackId = BuildCallbackId(definition.ScheduleId);
        var actorId = BuildActorId(definition.ScheduleId);
        await EnsureWakeupActorAsync(actorId, ct);
        var lease = await _callbacks.ScheduleTimeoutAsync(
            new RuntimeCallbackTimeoutRequest
            {
                ActorId = actorId,
                CallbackId = callbackId,
                DueTime = dueTime,
                TriggerEnvelope = CreateDueEnvelope(actorId, definition.ScheduleId, fireAtUtc),
                DeliveryMode = RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
            },
            ct);

        await _store.UpdateAsync(definition with { WakeupLease = ToScheduleLease(lease) }, ct);
    }

    private async Task EnsureWakeupActorAsync(
        string actorId,
        CancellationToken ct)
    {
        if (await _runtime.ExistsAsync(actorId))
            return;

        await _runtime.CreateAsync(typeof(WorkflowScheduleWakeupGAgent), actorId, ct);
    }

    public async Task CancelAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ct.ThrowIfCancellationRequested();

        var definition = await _store.GetAsync(scheduleId, ct);
        if (definition?.WakeupLease == null)
            return;

        await CancelLeaseAsync(definition.WakeupLease, ct);
        await _store.UpdateAsync(definition with { WakeupLease = null }, ct);
    }

    private async Task CancelLeaseAsync(
        WorkflowScheduleWakeupLease? lease,
        CancellationToken ct)
    {
        if (lease == null)
            return;

        await _callbacks.CancelAsync(ToRuntimeLease(lease), ct);
    }

    private static EventEnvelope CreateDueEnvelope(
        string actorId,
        string scheduleId,
        DateTimeOffset fireAtUtc)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new WorkflowScheduleDueEvent
            {
                ScheduleId = scheduleId,
                ScheduledFireAtUnixTimeMs = fireAtUtc.ToUnixTimeMilliseconds(),
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(actorId, TopologyAudience.Self),
        };
    }

    private static string BuildCallbackId(string scheduleId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(CallbackIdPrefix, scheduleId);

    private static string BuildActorId(string scheduleId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(ActorIdPrefix, scheduleId);

    private static WorkflowScheduleWakeupLease ToScheduleLease(RuntimeCallbackLease lease)
    {
        return new WorkflowScheduleWakeupLease(
            lease.ActorId,
            lease.CallbackId,
            lease.Generation,
            lease.Backend == RuntimeCallbackBackend.Dedicated
                ? WorkflowScheduleWakeupBackend.Dedicated
                : WorkflowScheduleWakeupBackend.InMemory,
            lease.SlotEpoch);
    }

    private static RuntimeCallbackLease ToRuntimeLease(WorkflowScheduleWakeupLease lease)
    {
        return new RuntimeCallbackLease(
            lease.ActorId,
            lease.CallbackId,
            lease.Generation,
            lease.Backend == WorkflowScheduleWakeupBackend.Dedicated
                ? RuntimeCallbackBackend.Dedicated
                : RuntimeCallbackBackend.InMemory)
        {
            SlotEpoch = lease.SlotEpoch,
        };
    }
}
