using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeCallbackSchedulerGrainRecoveryTests
{
    [Fact]
    public void TryUpsertCoalescedTimeout_ShouldKeepOnlyLatestPendingAuthoritativeSequence()
    {
        const string actorId = "status-materializer";
        const string callbackId = "latest-source-observation";
        const string coalescingKey = "source-scope";
        var state = new RuntimeCallbackSchedulerState();
        var scheduledAt = DateTimeOffset.Parse(
            "2026-08-28T00:00:00Z",
            CultureInfo.InvariantCulture);

        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("source-v10"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 10,
                coalescingValueIdentity: "evt-v10",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var firstGeneration)
            .Should().BeTrue();

        firstGeneration.Should().Be(1);
        state.CoalescingWatermarks[coalescingKey].Sequence.Should().Be(10);
        state.ReminderCallbacks[callbackId].TriggerEnvelope.Id.Should().Be("source-v10");

        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("duplicate-source-v10"),
                dueTimeMs: 10000,
                coalescingKey,
                coalescingSequence: 10,
                coalescingValueIdentity: "evt-v10",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var reusedGeneration)
            .Should().BeFalse();

        reusedGeneration.Should().Be(firstGeneration);
        state.ReminderCallbacks[callbackId].TriggerEnvelope.Id.Should().Be("source-v10");

        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("source-v11"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 11,
                coalescingValueIdentity: "evt-v11",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var newerGeneration)
            .Should().BeTrue();

        newerGeneration.Should().Be(2);
        state.CoalescingWatermarks[coalescingKey].Sequence.Should().Be(11);
        state.ReminderCallbacks[callbackId].TriggerEnvelope.Id.Should().Be("source-v11");

        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("stale-source-v10"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 10,
                coalescingValueIdentity: "evt-v10",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var staleGeneration)
            .Should().BeFalse();

        staleGeneration.Should().Be(newerGeneration);
        state.ReminderCallbacks[callbackId].TriggerEnvelope.Id.Should().Be("source-v11");

        var fired = state.ReminderCallbacks[callbackId].Clone();
        state.ReminderCallbacks[callbackId].PendingDeliveryEnvelope = CreateEnvelope("fired-source-v11");
        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("retry-while-source-v11-is-firing"),
                dueTimeMs: 10000,
                coalescingKey,
                coalescingSequence: 11,
                coalescingValueIdentity: "evt-v11",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var firingRaceGeneration)
            .Should().BeTrue();

        firingRaceGeneration.Should().Be(3);
        RuntimeCallbackSchedulerGrain.TryClearCompletedOneShotCallback(
                state,
                callbackId,
                fired)
            .Should().BeFalse("a fired generation cannot clear the retry scheduled by its handler");
        var current = state.ReminderCallbacks[callbackId].Clone();
        RuntimeCallbackSchedulerGrain.TryClearCompletedOneShotCallback(
                state,
                callbackId,
                current)
            .Should().BeTrue();
        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("retry-source-v11"),
                dueTimeMs: 10000,
                coalescingKey,
                coalescingSequence: 11,
                coalescingValueIdentity: "evt-v11",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var rescheduledGeneration)
            .Should().BeTrue();

        rescheduledGeneration.Should().Be(4);
        state.ReminderCallbacks[callbackId].TriggerEnvelope.Id.Should().Be("retry-source-v11");
    }

    [Fact]
    public void TryCompleteCoalescedTimeout_ShouldCancelPendingAndFenceDelayedOlderRetry()
    {
        const string actorId = "status-materializer";
        const string callbackId = "latest-source-observation";
        const string coalescingKey = "source-scope";
        var state = new RuntimeCallbackSchedulerState();
        var scheduledAt = DateTimeOffset.Parse(
            "2026-08-28T00:00:00Z",
            CultureInfo.InvariantCulture);
        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("source-v10"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 10,
                coalescingValueIdentity: "evt-v10",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var firstGeneration)
            .Should().BeTrue();

        RuntimeCallbackSchedulerGrain.TryCompleteCoalescedTimeout(
                state,
                callbackId,
                coalescingKey,
                coalescingSequence: 11,
                coalescingValueIdentity: "evt-v11",
                coalescingPrecedence: 0,
                out var reminderUnregistrationRequired)
            .Should().BeTrue();

        reminderUnregistrationRequired.Should().BeTrue();
        state.ReminderCallbacks.Should().NotContainKey(callbackId);
        state.CoalescingWatermarks[coalescingKey].Sequence.Should().Be(11);
        state.CoalescingWatermarks[coalescingKey].ValueIdentity.Should().Be("evt-v11");
        state.CoalescingWatermarks[coalescingKey].Completed.Should().BeTrue();

        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("delayed-source-v10"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 10,
                coalescingValueIdentity: "evt-v10",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var delayedGeneration)
            .Should().BeFalse();
        delayedGeneration.Should().Be(firstGeneration);
        state.ReminderCallbacks.Should().NotContainKey(callbackId);

        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("source-v12"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 12,
                coalescingValueIdentity: "evt-v12",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var newerGeneration)
            .Should().BeTrue();
        newerGeneration.Should().Be(firstGeneration + 1);
        state.CoalescingWatermarks[coalescingKey].Completed.Should().BeFalse();
    }

    [Fact]
    public void TryCompleteCoalescedTimeout_AtSameSequenceWithDifferentIdentity_ShouldFailClosed()
    {
        const string actorId = "status-materializer";
        const string callbackId = "latest-source-observation";
        const string coalescingKey = "source-scope";
        var state = new RuntimeCallbackSchedulerState();
        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
            state,
            actorId,
            callbackId,
            CreateEnvelope("source-v10"),
            dueTimeMs: 5000,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "evt-v10",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
            DateTimeOffset.UtcNow,
            out _);

        var complete = () => RuntimeCallbackSchedulerGrain.TryCompleteCoalescedTimeout(
            state,
            callbackId,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "evt-conflict-v10",
            coalescingPrecedence: 0,
            out _);

        complete.Should().Throw<InvalidOperationException>()
            .WithMessage("*conflicting identities*");
        state.ReminderCallbacks.Should().ContainKey(callbackId);
    }

    [Fact]
    public void TryCompleteCoalescedTimeout_WhenPendingCursorDriftsFromWatermark_ShouldFailClosed()
    {
        const string actorId = "status-materializer";
        const string callbackId = "latest-source-observation";
        const string coalescingKey = "source-scope";
        var state = new RuntimeCallbackSchedulerState();
        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
            state,
            actorId,
            callbackId,
            CreateEnvelope("source-v10"),
            dueTimeMs: 5000,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "publication-v10",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
            DateTimeOffset.UtcNow,
            out _);
        state.ReminderCallbacks[callbackId].CoalescingValueIdentity =
            "corrupted-pending-publication-v10";

        var complete = () => RuntimeCallbackSchedulerGrain.TryCompleteCoalescedTimeout(
            state,
            callbackId,
            coalescingKey,
            coalescingSequence: 11,
            coalescingValueIdentity: "publication-v11",
            coalescingPrecedence: 0,
            out _);

        complete.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not match its coalescing watermark*");
        state.ReminderCallbacks.Should().ContainKey(callbackId);
        state.CoalescingWatermarks[coalescingKey].Sequence.Should().Be(10);
        state.CoalescingWatermarks[coalescingKey].Completed.Should().BeFalse();
    }

    [Fact]
    public void TryCompleteCoalescedTimeout_NewerSuccess_ShouldAdvanceCompletedWatermark()
    {
        const string callbackId = "latest-source-observation";
        const string coalescingKey = "source-scope";
        var state = new RuntimeCallbackSchedulerState
        {
            CallbackGenerations = { [callbackId] = 3 },
            CoalescingWatermarks =
            {
                [coalescingKey] = new RuntimeCallbackCoalescingWatermark
                {
                    CallbackId = callbackId,
                    Sequence = 10,
                    ValueIdentity = "evt-v10",
                    Completed = true,
                },
            },
        };

        RuntimeCallbackSchedulerGrain.TryCompleteCoalescedTimeout(
                state,
                callbackId,
                coalescingKey,
                coalescingSequence: 11,
                coalescingValueIdentity: "evt-v11",
                coalescingPrecedence: 0,
                out var reminderUnregistrationRequired)
            .Should().BeTrue();

        reminderUnregistrationRequired.Should().BeFalse();
        state.CoalescingWatermarks[coalescingKey].Sequence.Should().Be(11);
        state.CoalescingWatermarks[coalescingKey].ValueIdentity.Should().Be("evt-v11");
        state.CoalescingWatermarks[coalescingKey].Completed.Should().BeTrue();
    }

    [Fact]
    public void TryCompleteCoalescedTimeout_BeforeAnyFailure_ShouldFenceDelayedOlderRetry()
    {
        const string actorId = "status-materializer";
        const string callbackId = "latest-source-observation";
        const string coalescingKey = "source-scope";
        var state = new RuntimeCallbackSchedulerState();

        RuntimeCallbackSchedulerGrain.TryCompleteCoalescedTimeout(
                state,
                callbackId,
                coalescingKey,
                coalescingSequence: 11,
                coalescingValueIdentity: "evt-v11",
                coalescingPrecedence: 0,
                out var reminderUnregistrationRequired)
            .Should().BeTrue();

        reminderUnregistrationRequired.Should().BeFalse();
        state.CallbackGenerations[callbackId].Should().Be(1);
        state.CoalescingWatermarks[coalescingKey].Completed.Should().BeTrue();
        state.CoalescingWatermarks[coalescingKey].Sequence.Should().Be(11);
        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("delayed-source-v10"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 10,
                coalescingValueIdentity: "evt-v10",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                DateTimeOffset.UtcNow,
                out var delayedGeneration)
            .Should().BeFalse();
        delayedGeneration.Should().Be(1);
        state.ReminderCallbacks.Should().NotContainKey(callbackId);

        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("source-v12"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 12,
                coalescingValueIdentity: "evt-v12",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                DateTimeOffset.UtcNow,
                out var newerGeneration)
            .Should().BeTrue();
        newerGeneration.Should().Be(2);
        state.ReminderCallbacks.Should().ContainKey(callbackId);
    }

    [Fact]
    public void TryUpsertCoalescedTimeout_WithManyLineages_ShouldRemainBoundedByAuthoritativeSource()
    {
        const int sourceCount = 32;
        const int versionsPerSource = 64;
        var state = new RuntimeCallbackSchedulerState();
        var scheduledAt = DateTimeOffset.Parse(
            "2026-08-28T00:00:00Z",
            CultureInfo.InvariantCulture);

        for (var sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            var coalescingKey = $"source-scope-{sourceIndex}";
            var callbackId = $"latest-source-observation-{sourceIndex}";
            for (var sequence = 1; sequence <= versionsPerSource; sequence++)
            {
                RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                        state,
                        "status-materializer",
                        callbackId,
                        CreateEnvelope($"{coalescingKey}-v{sequence}"),
                        dueTimeMs: 5000,
                        coalescingKey,
                        coalescingSequence: sequence,
                        coalescingValueIdentity: $"evt-{coalescingKey}-v{sequence}",
                        coalescingPrecedence: 0,
                        RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                        scheduledAt,
                        out _)
                    .Should().BeTrue();
            }
        }

        state.ReminderCallbacks.Should().HaveCount(sourceCount);
        state.CoalescingWatermarks.Should().HaveCount(sourceCount);
        state.CoalescingWatermarks.Values.Should()
            .OnlyContain(watermark => watermark.Sequence == versionsPerSource);
        state.ReminderCallbacks.Values.Should()
            .OnlyContain(callback => callback.CoalescingSequence == versionsPerSource);
    }

    [Fact]
    public void TryUpsertCoalescedTimeout_AtSameSequence_ShouldFenceConflictAndOrderPrecedence()
    {
        const string actorId = "status-materializer";
        const string callbackId = "latest-source-observation";
        const string coalescingKey = "source-scope";
        var state = new RuntimeCallbackSchedulerState();
        var scheduledAt = DateTimeOffset.Parse(
            "2026-08-28T00:00:00Z",
            CultureInfo.InvariantCulture);

        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("ordinary-v10"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 10,
                coalescingValueIdentity: "evt-ordinary-v10",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out _)
            .Should().BeTrue();

        var conflict = () => RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
            state,
            actorId,
            callbackId,
            CreateEnvelope("conflicting-v10"),
            dueTimeMs: 5000,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "evt-conflicting-v10",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
            scheduledAt,
            out _);

        conflict.Should().Throw<InvalidOperationException>()
            .WithMessage("*conflicting identities*");

        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("maintenance-v10"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 10,
                coalescingValueIdentity: "rebuild:source-scope:10",
                coalescingPrecedence: 1,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var maintenanceGeneration)
            .Should().BeTrue();

        state.CoalescingWatermarks[coalescingKey].ValueIdentity.Should()
            .Be("rebuild:source-scope:10");
        state.CoalescingWatermarks[coalescingKey].Precedence.Should().Be(1);
        RuntimeCallbackSchedulerGrain.TryUpsertCoalescedTimeout(
                state,
                actorId,
                callbackId,
                CreateEnvelope("delayed-ordinary-v10"),
                dueTimeMs: 5000,
                coalescingKey,
                coalescingSequence: 10,
                coalescingValueIdentity: "evt-ordinary-v10",
                coalescingPrecedence: 0,
                RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                scheduledAt,
                out var staleGeneration)
            .Should().BeFalse();
        staleGeneration.Should().Be(maintenanceGeneration);
    }

    [Fact]
    public void ResolveRollingUpgradeCoalescingCursor_ShouldUseCanonicalCommittedPublicationIdentity()
    {
        var published = new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                AgentId = "source-scope",
                EventId = CommittedStateRepublish.BuildEventId("source-scope", 10),
                Version = 10,
                EventData = Any.Pack(new StringValue { Value = "maintenance" }),
            },
            StateRoot = Any.Pack(new StringValue { Value = "current-state" }),
        };
        var envelope = CreateEnvelope("rolling-upgrade-v10");
        envelope.Payload = Any.Pack(published);

        var cursor = RuntimeCallbackSchedulerGrain.ResolveRollingUpgradeCoalescingCursor(
            envelope,
            "source-scope",
            10);

        cursor.Should().Be(new RuntimeEnvelopeRetryCoalescingCursor(
            "source-scope",
            10,
            RuntimeEnvelopeRetryCoalescingValueIdentity.Create(published),
            precedence: 1));
    }

    [Fact]
    public void TryClearCompletedOneShotCallback_WhenCallbackWasRescheduled_ShouldKeepNewGeneration()
    {
        const string callbackId = "scheduled-dispatch-next-fire";
        var firedCallback = CreateScheduledCallback(callbackId, generation: 7);
        var state = new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                [callbackId] = CreateScheduledCallback(callbackId, generation: 8),
            },
        };

        var cleared = RuntimeCallbackSchedulerGrain.TryClearCompletedOneShotCallback(
            state,
            callbackId,
            firedCallback);

        cleared.Should().BeFalse();
        state.ReminderCallbacks.Should().ContainKey(callbackId);
        state.ReminderCallbacks[callbackId].Generation.Should().Be(8);
    }

    [Fact]
    public void TryClearCompletedOneShotCallback_WhenCallbackStillCurrent_ShouldClearCallback()
    {
        const string callbackId = "scheduled-dispatch-next-fire";
        var firedCallback = CreateScheduledCallback(callbackId, generation: 7);
        var state = new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                [callbackId] = firedCallback.Clone(),
            },
        };

        var cleared = RuntimeCallbackSchedulerGrain.TryClearCompletedOneShotCallback(
            state,
            callbackId,
            firedCallback);

        cleared.Should().BeTrue();
        state.ReminderCallbacks.Should().NotContainKey(callbackId);
    }

    [Fact]
    public async Task OnActivateAsync_WhenDurableTimeoutIsOverdue_ShouldPublishAndClearOneShotCallback()
    {
        const string actorId = "scheduled-recovery-actor";
        const string callbackId = "scheduled-dispatch-next-fire";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        using var host = await StartSiloHostAsync(streamProvider, storage);

        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        storage.SeedSchedulerState(grain.GetGrainId(), new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                [callbackId] = new RuntimeScheduledCallback
                {
                    ActorId = actorId,
                    CallbackId = callbackId,
                    Generation = 7,
                    SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
                    Periodic = false,
                    DueTimeMillis = 1000,
                    PeriodMillis = 0,
                    FireIndex = 0,
                    DeliveryMode = RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent,
                    TriggerEnvelope = CreateEnvelope("evt-overdue"),
                    NextDueAtUnixTimeMs = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                    OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
                },
            },
        });

        await grain.CancelAsync("unrelated-callback");

        var produced = streamProvider.GetProduced(actorId).Should().ContainSingle().Subject;
        produced.Payload.Unpack<StringValue>().Value.Should().Be("payload");
        produced.Runtime!.Callback.CallbackId.Should().Be(callbackId);
        produced.Runtime.Callback.Generation.Should().Be(7);
        produced.Runtime.Callback.FireIndex.Should().Be(1);
        produced.Runtime.Callback.SlotEpoch.Should().Be(RuntimeCallbackSlotEpoch.OrleansSchedulerV2);

        var state = storage.ReadSchedulerState(grain.GetGrainId());
        state.ReminderCallbacks.Should().NotContainKey(callbackId);
        state.CallbackGenerations[callbackId].Should().Be(7);
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_AfterOneShotCleanup_ShouldNotReuseFiredGeneration()
    {
        const string actorId = "scheduled-recovery-actor";
        const string callbackId = "scheduled-dispatch-next-fire";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        using var host = await StartSiloHostAsync(streamProvider, storage);

        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        var firedCallback = CreateScheduledCallback(callbackId, generation: 7);
        firedCallback.NextDueAtUnixTimeMs = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        storage.SeedSchedulerState(grain.GetGrainId(), new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                [callbackId] = firedCallback,
            },
        });

        await grain.CancelAsync("unrelated-callback");
        var generation = await grain.ScheduleTimeoutAsync(
            callbackId,
            CreateEnvelope("evt-next"),
            dueTimeMs: 60000);
        await grain.CancelAsync(
            callbackId,
            expectedGeneration: 7,
            expectedSlotEpoch: RuntimeCallbackSlotEpoch.OrleansSchedulerV2);

        generation.Should().Be(8);
        var state = storage.ReadSchedulerState(grain.GetGrainId());
        state.ReminderCallbacks.Should().ContainKey(callbackId);
        state.ReminderCallbacks[callbackId].Generation.Should().Be(8);
        state.CallbackGenerations[callbackId].Should().Be(8);
    }

    [Fact]
    public async Task ScheduleCoalescedTimeoutAsync_ShouldPersistLatestEnvelopeAndHighWatermarkTogether()
    {
        const string actorId = "status-materializer-coalescing";
        const string callbackId = "latest-source-observation";
        const string coalescingKey = "source-scope";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        var reminderTable = new RecordingReminderTable();
        using var host = await StartSiloHostAsync(streamProvider, storage, reminderTable);
        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);

        var first = await grain.ScheduleCoalescedTimeoutAsync(
            callbackId,
            CreateEnvelope("source-v10"),
            dueTimeMs: 600_000,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "evt-v10",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery);
        var duplicate = await grain.ScheduleCoalescedTimeoutAsync(
            callbackId,
            CreateEnvelope("duplicate-source-v10"),
            dueTimeMs: 600_000,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "evt-v10",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery);
        var newer = await grain.ScheduleCoalescedTimeoutAsync(
            callbackId,
            CreateEnvelope("source-v11"),
            dueTimeMs: 600_000,
            coalescingKey,
            coalescingSequence: 11,
            coalescingValueIdentity: "evt-v11",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery);
        var stale = await grain.ScheduleCoalescedTimeoutAsync(
            callbackId,
            CreateEnvelope("stale-source-v10"),
            dueTimeMs: 600_000,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "evt-v10",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery);

        duplicate.Should().Be(first);
        newer.Should().Be(first + 1);
        stale.Should().Be(newer);
        var state = storage.ReadSchedulerState(grain.GetGrainId());
        state.CoalescingWatermarks[coalescingKey].Sequence.Should().Be(11);
        state.ReminderCallbacks[callbackId].CoalescingSequence.Should().Be(11);
        state.ReminderCallbacks[callbackId].TriggerEnvelope.Id.Should().Be("source-v11");
        reminderTable.Contains(grain.GetGrainId(), callbackId).Should().BeTrue();
        await FluentActions.Awaiting(() => grain.CancelAsync(
                callbackId,
                newer,
                RuntimeCallbackSlotEpoch.OrleansSchedulerV2))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*coalesced timeout contract*");
        await grain.PurgeAsync();
    }

    [Fact]
    public async Task CompleteCoalescedTimeoutAsync_WhenUnregistrationFails_ShouldKeepCompletedFenceAndRecover()
    {
        const string actorId = "status-materializer-completion-recovery";
        const string callbackId = "latest-source-observation";
        const string coalescingKey = "source-scope";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        var reminderTable = new RecordingReminderTable();
        using var host = await StartSiloHostAsync(streamProvider, storage, reminderTable);
        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        var grainId = grain.GetGrainId();

        await grain.ScheduleCoalescedTimeoutAsync(
            callbackId,
            CreateEnvelope("source-v10"),
            dueTimeMs: 600_000,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "publication-v10",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery);
        reminderTable.FailNextRemoval(callbackId);

        var firstCompletion = () => grain.CompleteCoalescedTimeoutAsync(
            callbackId,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "publication-v10",
            coalescingPrecedence: 0);

        await firstCompletion.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"forced unregister failure: {callbackId}");
        var committedFence = storage.ReadSchedulerState(grainId);
        committedFence.ReminderCallbacks.Should().NotContainKey(callbackId);
        committedFence.CoalescingWatermarks[coalescingKey].Completed.Should().BeTrue();
        committedFence.PendingReminderUnregistrations.Should().ContainSingle()
            .Which.Should().Be(callbackId);
        reminderTable.Contains(grainId, callbackId).Should().BeTrue();

        await grain.CompleteCoalescedTimeoutAsync(
            callbackId,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "publication-v10",
            coalescingPrecedence: 0);

        var recovered = storage.ReadSchedulerState(grainId);
        recovered.ReminderCallbacks.Should().NotContainKey(callbackId);
        recovered.CoalescingWatermarks[coalescingKey].Completed.Should().BeTrue();
        recovered.PendingReminderUnregistrations.Should().BeEmpty();
        reminderTable.Contains(grainId, callbackId).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteCoalescedTimeoutAsync_BeforeAnyFailure_ShouldPersistFenceForDelayedOlderRetry()
    {
        const string actorId = "status-materializer-success-first";
        const string coalescingKey = "source-scope";
        var callbackId = RuntimeEnvelopeRetryCoalescingCallbackSlot.BuildCallbackId(coalescingKey);
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        var reminderTable = new RecordingReminderTable();
        using var host = await StartSiloHostAsync(streamProvider, storage, reminderTable);
        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        var grainId = grain.GetGrainId();

        await grain.CompleteCoalescedTimeoutAsync(
            callbackId,
            coalescingKey,
            coalescingSequence: 11,
            coalescingValueIdentity: "evt-v11",
            coalescingPrecedence: 0);

        var completed = storage.ReadSchedulerState(grainId);
        completed.CallbackGenerations[callbackId].Should().Be(1);
        completed.CoalescingWatermarks[coalescingKey].Completed.Should().BeTrue();
        completed.CoalescingWatermarks[coalescingKey].Sequence.Should().Be(11);
        var delayedGeneration = await grain.ScheduleCoalescedTimeoutAsync(
            callbackId,
            CreateEnvelope("delayed-source-v10"),
            dueTimeMs: 600_000,
            coalescingKey,
            coalescingSequence: 10,
            coalescingValueIdentity: "evt-v10",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery);

        delayedGeneration.Should().Be(1);
        storage.ReadSchedulerState(grainId).ReminderCallbacks.Should().NotContainKey(callbackId);
        reminderTable.Contains(grainId, callbackId).Should().BeFalse();

        var newerGeneration = await grain.ScheduleCoalescedTimeoutAsync(
            callbackId,
            CreateEnvelope("source-v12"),
            dueTimeMs: 600_000,
            coalescingKey,
            coalescingSequence: 12,
            coalescingValueIdentity: "evt-v12",
            coalescingPrecedence: 0,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery);

        newerGeneration.Should().Be(2);
        storage.ReadSchedulerState(grainId).ReminderCallbacks.Should().ContainKey(callbackId);
        reminderTable.Contains(grainId, callbackId).Should().BeTrue();
        await grain.PurgeAsync();
    }

    [Fact]
    public async Task PurgeAsync_WithOrleansReminderRegistry_ShouldPreserveGrainContextAcrossUnregistration()
    {
        const string actorId = "scheduled-real-reminder-purge-actor";
        const string callbackId = "scheduled-dispatch-next-fire";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        using var host = await StartSiloHostAsync(streamProvider, storage);

        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        await grain.ScheduleTimeoutAsync(
            callbackId,
            CreateEnvelope("evt-real-reminder"),
            dueTimeMs: 600_000);

        var purge = () => grain.PurgeAsync();

        await purge.Should().NotThrowAsync();
        var state = storage.ReadSchedulerState(grain.GetGrainId());
        state.ReminderCallbacks.Should().BeEmpty();
        state.CallbackGenerations.Should().BeEmpty();
        state.PendingReminderUnregistrations.Should().BeEmpty();
    }

    [Fact]
    public async Task ReminderTick_WhenOneShotCompletes_ShouldUnregisterWithinGrainContext()
    {
        const string actorId = "scheduled-real-reminder-tick-actor";
        const string callbackId = "scheduled-dispatch-next-fire";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        var reminderTable = new RecordingReminderTable();
        var failureLoggerProvider = new GrainContextFailureLoggerProvider();
        using var host = await StartSiloHostAsync(
            streamProvider,
            storage,
            reminderTable,
            failureLoggerProvider);

        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        var grainId = grain.GetGrainId();
        var removalTask = reminderTable.WaitForRemovalAsync(grainId, callbackId);
        var failureTask = failureLoggerProvider.WaitForNonGrainContextFailureAsync();
        var producedTask = streamProvider.WaitForProducedAsync(actorId);

        await grain.ScheduleTimeoutAsync(
            callbackId,
            CreateEnvelope("evt-real-reminder-tick"),
            dueTimeMs: 100);

        var produced = await producedTask.WaitAsync(TimeSpan.FromSeconds(30));
        produced.Runtime!.Callback.CallbackId.Should().Be(callbackId);
        produced.Runtime.Callback.Generation.Should().Be(1);
        produced.Runtime.Callback.FireIndex.Should().Be(1);
        var terminalSignal = await Task.WhenAny(removalTask, failureTask)
            .WaitAsync(TimeSpan.FromSeconds(30));

        terminalSignal.Should().BeSameAs(
            removalTask,
            "a completed reminder tick must unregister through an Orleans grain context");
        reminderTable.Contains(grainId, callbackId).Should().BeFalse();
        var state = storage.ReadSchedulerState(grainId);
        state.ReminderCallbacks.Should().BeEmpty();
        state.CallbackGenerations[callbackId].Should().Be(1);
    }

    [Fact]
    public async Task PurgeAsync_WhenSecondReminderUnregistrationFails_ShouldReplayToTerminalState()
    {
        const string actorId = "scheduled-purge-recovery-actor";
        const string firstCallbackId = "scheduled-dispatch-credential-expiry";
        const string secondCallbackId = "scheduled-dispatch-next-fire";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        var reminderTable = new RecordingReminderTable();
        using var host = await StartSiloHostAsync(streamProvider, storage, reminderTable);

        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        var grainId = grain.GetGrainId();
        storage.SeedSchedulerState(grainId, new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                [firstCallbackId] = CreateScheduledCallback(firstCallbackId, generation: 3),
                [secondCallbackId] = CreateScheduledCallback(secondCallbackId, generation: 5),
            },
            CallbackGenerations =
            {
                [firstCallbackId] = 3,
                [secondCallbackId] = 5,
            },
        });
        reminderTable.Seed(grainId, firstCallbackId);
        reminderTable.Seed(grainId, secondCallbackId);
        reminderTable.FailNextRemoval(secondCallbackId);

        var firstPurge = () => grain.PurgeAsync();

        await firstPurge.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"forced unregister failure: {secondCallbackId}");
        var pending = storage.ReadSchedulerState(grainId);
        pending.ReminderCallbacks.Should().BeEmpty();
        pending.CallbackGenerations.Should().BeEmpty();
        pending.PendingReminderUnregistrations.Should().BeEquivalentTo(
            [firstCallbackId, secondCallbackId]);
        reminderTable.Contains(grainId, firstCallbackId).Should().BeFalse();
        reminderTable.Contains(grainId, secondCallbackId).Should().BeTrue();

        await grain.PurgeAsync();

        var terminal = storage.ReadSchedulerState(grainId);
        terminal.ReminderCallbacks.Should().BeEmpty();
        terminal.CallbackGenerations.Should().BeEmpty();
        terminal.PendingReminderUnregistrations.Should().BeEmpty();
        reminderTable.Contains(grainId, firstCallbackId).Should().BeFalse();
        reminderTable.Contains(grainId, secondCallbackId).Should().BeFalse();
        reminderTable.RemovalAttempts.Should().Equal(
            firstCallbackId,
            secondCallbackId,
            secondCallbackId);
    }

    [Fact]
    public async Task OnActivateAsync_WhenPendingUnregistrationFails_ShouldBlockNewScheduleUntilRecovery()
    {
        const string actorId = "scheduled-pending-activation-actor";
        const string pendingCallbackId = "scheduled-dispatch-next-fire";
        const string newCallbackId = "scheduled-dispatch-new-fire";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        var reminderTable = new RecordingReminderTable();
        using var host = await StartSiloHostAsync(streamProvider, storage, reminderTable);

        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        var grainId = grain.GetGrainId();
        storage.SeedSchedulerState(grainId, new RuntimeCallbackSchedulerState
        {
            PendingReminderUnregistrations = { pendingCallbackId },
        });
        reminderTable.Seed(grainId, pendingCallbackId);
        reminderTable.FailNextRemoval(pendingCallbackId);

        var firstSchedule = () => grain.ScheduleTimeoutAsync(
            newCallbackId,
            CreateEnvelope("evt-new"),
            dueTimeMs: 60_000);

        await firstSchedule.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"forced unregister failure: {pendingCallbackId}");
        var blocked = storage.ReadSchedulerState(grainId);
        blocked.PendingReminderUnregistrations.Should().ContainSingle()
            .Which.Should().Be(pendingCallbackId);
        blocked.ReminderCallbacks.Should().NotContainKey(newCallbackId);
        reminderTable.Contains(grainId, pendingCallbackId).Should().BeTrue();
        reminderTable.Contains(grainId, newCallbackId).Should().BeFalse();

        await grain.ScheduleTimeoutAsync(
            newCallbackId,
            CreateEnvelope("evt-new"),
            dueTimeMs: 60_000);

        var recovered = storage.ReadSchedulerState(grainId);
        recovered.PendingReminderUnregistrations.Should().BeEmpty();
        recovered.ReminderCallbacks.Should().ContainKey(newCallbackId);
        reminderTable.Contains(grainId, pendingCallbackId).Should().BeFalse();
        reminderTable.Contains(grainId, newCallbackId).Should().BeTrue();
    }

    [Fact]
    public async Task PurgeAsync_WhenOnlyLegacyOrphanReminderExists_ShouldDiscoverAndUnregisterIt()
    {
        const string actorId = "scheduled-orphan-purge-actor";
        const string callbackId = "scheduled-dispatch-next-fire";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        var reminderTable = new RecordingReminderTable();
        using var host = await StartSiloHostAsync(streamProvider, storage, reminderTable);

        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        var grainId = grain.GetGrainId();
        reminderTable.Seed(grainId, callbackId);

        await grain.PurgeAsync();

        reminderTable.Contains(grainId, callbackId).Should().BeFalse();
        var terminal = storage.ReadSchedulerState(grainId);
        terminal.ReminderCallbacks.Should().BeEmpty();
        terminal.CallbackGenerations.Should().BeEmpty();
        terminal.PendingReminderUnregistrations.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureRuntimeFleetReconcileTimerAsync_WhenStateExistsWithoutPhysicalReminder_ShouldRepairInPlace()
    {
        const long generation = 7;
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        var reminderTable = new RecordingReminderTable();
        using var host = await StartSiloHostAsync(
            streamProvider,
            storage,
            reminderTable,
            disableFleetAuthorityBootstrap: true);

        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        var grainId = grain.GetGrainId();
        storage.SeedSchedulerState(grainId, new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                [RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId] =
                    CreateFleetReconcileSchedule(generation),
            },
            CallbackGenerations =
            {
                [RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId] = generation,
            },
        });

        var first = await grain.EnsureRuntimeFleetReconcileTimerAsync();
        var second = await grain.EnsureRuntimeFleetReconcileTimerAsync();

        first.Should().Be(generation);
        second.Should().Be(generation);
        reminderTable.Contains(
            grainId,
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId).Should().BeTrue();
        var repaired = storage.ReadSchedulerState(grainId);
        repaired.ReminderCallbacks[
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId].Generation.Should()
            .Be(generation);
        repaired.CallbackGenerations[
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId].Should()
            .Be(generation);
    }

    private static async Task<IHost> StartSiloHostAsync(
        RecordingStreamProvider streamProvider,
        TestRuntimeCallbackSchedulerStateStorage storage,
        RecordingReminderTable? reminderTable = null,
        GrainContextFailureLoggerProvider? failureLoggerProvider = null,
        bool disableFleetAuthorityBootstrap = false) =>
        await SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                if (failureLoggerProvider != null)
                    logging.AddProvider(failureLoggerProvider);
            })
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: ports.SiloPort,
                    gatewayPort: ports.GatewayPort,
                    serviceId: $"aevatar-runtime-callback-recovery-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-runtime-callback-recovery-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                {
                    if (disableFleetAuthorityBootstrap)
                    {
                        var bootstrap = services.SingleOrDefault(descriptor =>
                            descriptor.ServiceType == typeof(ILifecycleParticipant<ISiloLifecycle>) &&
                            descriptor.ImplementationType ==
                                typeof(RuntimeFleetAuthoritySiloLifecycleParticipant));
                        if (bootstrap != null)
                            services.Remove(bootstrap);
                    }

                    if (reminderTable != null)
                    {
                        services.Replace(
                            ServiceDescriptor.Singleton<IReminderTable>(reminderTable));
                    }

                    services.RemoveAll<IStreamProvider>();
                    services.RemoveAll<OrleansStreamProviderAdapter>();
                    services.RemoveAll<IStreamLifecycleManager>();
                    services.AddSingleton<IStreamProvider>(streamProvider);
                    services.AddSingleton<IStreamLifecycleManager, NoopStreamLifecycleManager>();
                    services.RemoveAllKeyed<IGrainStorage>(OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName);
                    services.AddSingleton(storage);
                    services.AddGrainStorage<TestRuntimeCallbackSchedulerStateStorage>(
                        OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName,
                        (sp, _) => sp.GetRequiredService<TestRuntimeCallbackSchedulerStateStorage>());
                });
            })
            .Build());

    private static RuntimeScheduledCallback CreateScheduledCallback(string callbackId, long generation) => new()
    {
        ActorId = "scheduled-recovery-actor",
        CallbackId = callbackId,
        Generation = generation,
        SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
        Periodic = false,
        DueTimeMillis = 1000,
        PeriodMillis = 0,
        FireIndex = 0,
        DeliveryMode = RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent,
        TriggerEnvelope = CreateEnvelope($"evt-{generation}"),
        NextDueAtUnixTimeMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
    };

    private static RuntimeScheduledCallback CreateFleetReconcileSchedule(long generation) => new()
    {
        ActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
        CallbackId = RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
        Generation = generation,
        SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
        Periodic = true,
        DueTimeMillis = 1000,
        PeriodMillis = checked((int)RuntimeCallbackSchedulerGrain.FleetReconcilePeriod.TotalMilliseconds),
        FireIndex = 3,
        DeliveryMode = RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent,
        TriggerEnvelope = new EventEnvelope
        {
            Id = "fleet-reconcile-trigger",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                TopologyAudience.Self),
            Payload = Any.Pack(new RuntimeFleetReconcileRequested()),
        },
        NextDueAtUnixTimeMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
        OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
    };

    private static EventEnvelope CreateEnvelope(string id) => new()
    {
        Id = id,
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Payload = Any.Pack(new StringValue { Value = "payload" }),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication("scheduled-recovery-actor", TopologyAudience.Self),
    };

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, List<EventEnvelope>> _produced = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<EventEnvelope>> _productionSignals =
            new(StringComparer.Ordinal);

        public IStream GetStream(string actorId) => new RecordingStream(actorId, this);

        public IReadOnlyList<EventEnvelope> GetProduced(string actorId)
        {
            lock (_gate)
                return _produced.GetValueOrDefault(actorId)?.ToArray() ?? [];
        }

        /// <summary>
        /// Completes with the first envelope produced for <paramref name="actorId"/>.
        /// </summary>
        public Task<EventEnvelope> WaitForProducedAsync(string actorId)
        {
            lock (_gate)
            {
                if (!_productionSignals.TryGetValue(actorId, out var productionSignal))
                {
                    productionSignal = new TaskCompletionSource<EventEnvelope>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _productionSignals[actorId] = productionSignal;
                }

                return productionSignal.Task;
            }
        }

        private void AddProduced(string actorId, EventEnvelope envelope)
        {
            var recorded = envelope.Clone();
            TaskCompletionSource<EventEnvelope>? productionSignal;
            lock (_gate)
            {
                var values = _produced.GetValueOrDefault(actorId) ?? [];
                values.Add(recorded);
                _produced[actorId] = values;
                productionSignal = _productionSignals.GetValueOrDefault(actorId);
            }

            productionSignal?.TrySetResult(recorded);
        }

        private sealed class RecordingStream : IStream
        {
            private readonly RecordingStreamProvider _owner;

            public RecordingStream(string actorId, RecordingStreamProvider owner)
            {
                StreamId = actorId;
                _owner = owner;
            }

            public string StreamId { get; }

            // Production stream backends complete off the activation's task scheduler. Modelling
            // that here is what makes a lost grain execution context observable: a synchronously
            // completed task never suspends the awaiting grain method, so the context can never be
            // dropped and the reminder unregistration path is not actually exercised.
            public Task ProduceAsync<T>(T message, CancellationToken ct = default)
                where T : IMessage
            {
                ct.ThrowIfCancellationRequested();
                if (message is EventEnvelope envelope)
                    _owner.AddProduced(StreamId, envelope);

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ThreadPool.UnsafeQueueUserWorkItem(static state => state.SetResult(), completion, preferLocal: false);
                return completion.Task;
            }

            public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
                where T : IMessage, new()
            {
                _ = handler;
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
            }

            public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
            {
                _ = binding;
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
            {
                _ = targetStreamId;
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
            }
        }
    }

    private sealed class NoopStreamLifecycleManager : IStreamLifecycleManager
    {
        public void RemoveStream(string actorId)
        {
            _ = actorId;
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// In-memory <see cref="IReminderTable"/> that keeps the real Orleans reminder pipeline
    /// (<c>LocalReminderService</c> plus the physical reminder rows) in the test, while allowing
    /// row removal to be observed deterministically and to be failed on demand.
    /// </summary>
    private sealed class RecordingReminderTable : IReminderTable
    {
        private const string ReminderNamePrefix = "runtime-callback:";
        private readonly object _gate = new();
        private readonly Dictionary<(GrainId GrainId, string ReminderName), ReminderEntry> _rows = [];
        private readonly Dictionary<(GrainId GrainId, string ReminderName), TaskCompletionSource> _removalSignals = [];
        private readonly HashSet<string> _failNextRemovals = new(StringComparer.Ordinal);
        private readonly List<string> _removalAttempts = [];
        private long _eTagSequence;

        public IReadOnlyList<string> RemovalAttempts
        {
            get
            {
                lock (_gate)
                    return _removalAttempts.ToArray();
            }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            lock (_gate)
            {
                return Task.FromResult(new ReminderTableData(
                    _rows.Values.Where(entry => entry.GrainId == grainId).Select(Copy).ToArray()));
            }
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            lock (_gate)
            {
                return Task.FromResult(new ReminderTableData(
                    _rows.Values.Where(entry => IsInRange(entry.GrainId.GetUniformHashCode(), begin, end))
                        .Select(Copy)
                        .ToArray()));
            }
        }

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            lock (_gate)
            {
                return Task.FromResult(
                    _rows.TryGetValue((grainId, reminderName), out var entry) ? Copy(entry) : null!);
            }
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            lock (_gate)
            {
                var eTag = (++_eTagSequence).ToString(CultureInfo.InvariantCulture);
                var stored = Copy(entry);
                stored.ETag = eTag;
                _rows[(entry.GrainId, entry.ReminderName)] = stored;
                return Task.FromResult(eTag);
            }
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            TaskCompletionSource? removalSignal;
            lock (_gate)
            {
                if (!_rows.TryGetValue((grainId, reminderName), out var entry) || entry.ETag != eTag)
                    return Task.FromResult(false);

                var callbackId = ToCallbackId(reminderName);
                _removalAttempts.Add(callbackId);
                if (_failNextRemovals.Remove(callbackId))
                {
                    throw new InvalidOperationException(
                        $"forced unregister failure: {callbackId}");
                }

                _rows.Remove((grainId, reminderName));
                removalSignal = _removalSignals.GetValueOrDefault((grainId, reminderName));
            }

            removalSignal?.TrySetResult();
            return Task.FromResult(true);
        }

        public Task TestOnlyClearTable()
        {
            lock (_gate)
                _rows.Clear();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Completes once the physical reminder row for <paramref name="callbackId"/> is deleted.
        /// </summary>
        public Task WaitForRemovalAsync(GrainId grainId, string callbackId)
        {
            lock (_gate)
            {
                var key = (grainId, ReminderNamePrefix + callbackId);
                if (!_removalSignals.TryGetValue(key, out var removalSignal))
                {
                    removalSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _removalSignals[key] = removalSignal;
                }

                return removalSignal.Task;
            }
        }

        public void Seed(GrainId grainId, string callbackId)
        {
            lock (_gate)
            {
                var reminderName = ReminderNamePrefix + callbackId;
                _rows[(grainId, reminderName)] = new ReminderEntry
                {
                    GrainId = grainId,
                    ReminderName = reminderName,
                    StartAt = DateTime.UtcNow.AddMinutes(5),
                    Period = TimeSpan.FromMinutes(1),
                    ETag = (++_eTagSequence).ToString(CultureInfo.InvariantCulture),
                };
            }
        }

        public void FailNextRemoval(string callbackId)
        {
            lock (_gate)
                _failNextRemovals.Add(callbackId);
        }

        public bool Contains(GrainId grainId, string callbackId)
        {
            lock (_gate)
                return _rows.ContainsKey((grainId, ReminderNamePrefix + callbackId));
        }

        private static string ToCallbackId(string reminderName) =>
            reminderName.StartsWith(ReminderNamePrefix, StringComparison.Ordinal)
                ? reminderName[ReminderNamePrefix.Length..]
                : reminderName;

        private static bool IsInRange(uint hash, uint begin, uint end) =>
            begin < end
                ? hash > begin && hash <= end
                : hash > begin || hash <= end;

        private static ReminderEntry Copy(ReminderEntry entry) => new()
        {
            GrainId = entry.GrainId,
            ReminderName = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
            ETag = entry.ETag,
        };
    }

    /// <summary>
    /// Captures the Orleans log entry emitted when a reminder tick fails because the grain
    /// execution context was lost, so the test can fail fast instead of waiting for a timeout.
    /// </summary>
    private sealed class GrainContextFailureLoggerProvider : ILoggerProvider
    {
        private const string NonGrainContextMarker = "non-grain context";

        private readonly TaskCompletionSource _failureSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForNonGrainContextFailureAsync() => _failureSignal.Task;

        public ILogger CreateLogger(string categoryName) => new FailureCapturingLogger(this);

        public void Dispose()
        {
        }

        private void Observe(string message, Exception? exception)
        {
            if (message.Contains(NonGrainContextMarker, StringComparison.OrdinalIgnoreCase) ||
                exception?.ToString().Contains(NonGrainContextMarker, StringComparison.OrdinalIgnoreCase) == true)
            {
                _failureSignal.TrySetResult();
            }
        }

        private sealed class FailureCapturingLogger(GrainContextFailureLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _ = eventId;
                if (!IsEnabled(logLevel))
                    return;

                owner.Observe(formatter(state, exception), exception);
            }
        }
    }

    private sealed class TestRuntimeCallbackSchedulerStateStorage : IGrainStorage
    {
        private const string SchedulerStateName = "runtime-callback-scheduler-v2";
        private readonly Dictionary<(string StateName, GrainId GrainId), object> _states = new();

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            if (_states.TryGetValue((stateName, grainId), out var state))
            {
                grainState.State = CloneState((T)state);
                grainState.RecordExists = true;
                grainState.ETag = string.Empty;
            }

            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            _states[(stateName, grainId)] = CloneState(grainState.State)
                ?? throw new InvalidOperationException("Runtime callback scheduler state cannot be null.");
            grainState.RecordExists = true;
            grainState.ETag = string.Empty;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            _states.Remove((stateName, grainId));
            grainState.RecordExists = false;
            grainState.ETag = string.Empty;
            return Task.CompletedTask;
        }

        public void SeedSchedulerState(GrainId grainId, RuntimeCallbackSchedulerState state)
        {
            _states[(SchedulerStateName, grainId)] = state.Clone();
        }

        public RuntimeCallbackSchedulerState ReadSchedulerState(GrainId grainId)
        {
            var state = _states[(SchedulerStateName, grainId)];
            return ((RuntimeCallbackSchedulerState)state).Clone();
        }

        private static T CloneState<T>(T state)
        {
            if (state is IDeepCloneable<T> cloneable)
                return cloneable.Clone();

            return state;
        }
    }
}
