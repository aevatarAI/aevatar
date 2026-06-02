using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Tests;

public sealed class ScheduledDispatchGAgentTests
{
    private const string ScheduleActorId = "scheduled-dispatch:schedule-1";
    private const string NextFireCallbackId = "scheduled-dispatch-next-fire";

    [Fact]
    public async Task HandleFireAsync_ShouldSuppressDuplicateDispatchAfterStartedRecordIsDurable()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        dispatch.Dispatches.Should().ContainSingle();
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        agent.State.FireRecords.Should().ContainKey(idempotencyKey);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Dispatched);
    }

    [Fact]
    public async Task HandleConfigureAsync_WhenEnabled_ShouldRegisterDurableNextFireCallback()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();

        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        scheduler.TimeoutRequests.Should().ContainSingle();
        var request = scheduler.TimeoutRequests[0];
        request.ActorId.Should().Be(ScheduleActorId);
        request.CallbackId.Should().Be(NextFireCallbackId);
        request.DeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.FiredSelfEvent);
        request.DueTime.Should().BePositive();
        request.DueTime.Should().BeLessThan(TimeSpan.FromSeconds(70));

        var fireCommand = request.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        fireCommand.Manual.Should().BeFalse();
        fireCommand.ScheduledFireAt.Should().NotBeNull();
        var scheduledFireAt = fireCommand.ScheduledFireAt.ToDateTimeOffset();
        scheduledFireAt.Should().Be(agent.State.NextFireAt);

        agent.State.NextFireLease.Should().NotBeNull();
        agent.State.NextFireLease!.ActorId.Should().Be(ScheduleActorId);
        agent.State.NextFireLease.CallbackId.Should().Be(NextFireCallbackId);
        agent.State.NextFireLease.Generation.Should().Be(1);
        agent.State.NextFireLease.Backend.Should().Be(ScheduledDispatchRuntimeCallbackBackendState.Dedicated);
    }

    [Fact]
    public async Task HandleDisableAsync_ShouldCancelExistingLeaseBeforeDisabledStateClearsIt()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        await agent.HandleDisableAsync(new ScheduledDispatchDisableCommand
        {
            Reason = "pause",
        });

        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].ActorId.Should().Be(ScheduleActorId);
        scheduler.Canceled[0].CallbackId.Should().Be(NextFireCallbackId);
        scheduler.Canceled[0].Generation.Should().Be(1);
        agent.State.Enabled.Should().BeFalse();
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
    }

    [Fact]
    public async Task HandleConfigureAsync_WhenUpdatingToDisabled_ShouldCancelExistingLeaseBeforeConfiguredStateClearsIt()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        await agent.HandleConfigureAsync(CreateConfigureCommand(
            targetActorId: "target-actor-updated",
            cronExpression: "*/5 * * * *",
            enabled: false));

        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].ActorId.Should().Be(ScheduleActorId);
        scheduler.Canceled[0].CallbackId.Should().Be(NextFireCallbackId);
        scheduler.Canceled[0].Generation.Should().Be(1);
        scheduler.TimeoutRequests.Should().ContainSingle();
        agent.State.Enabled.Should().BeFalse();
        agent.State.TargetActorId.Should().Be("target-actor-updated");
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
    }

    [Fact]
    public async Task HandleEventAsync_WhenDueCallbackArrives_ShouldDispatchNonManualFireAndScheduleNext()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        var firstRequest = scheduler.TimeoutRequests.Single();
        var firstFireCommand = firstRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        var firstScheduledFireAt = firstFireCommand.ScheduledFireAt.ToDateTimeOffset();

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(firstRequest, generation: 1, fireIndex: 1));

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", firstScheduledFireAt);
        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("target-actor-1");
        dispatched.Envelope.Id.Should().Be(idempotencyKey);
        dispatched.Envelope.Route.GetTargetActorId().Should().Be("target-actor-1");
        var chatRequest = dispatched.Envelope.Payload.Unpack<ChatRequestEvent>();
        chatRequest.SessionId.Should().Be(idempotencyKey);
        chatRequest.Headers.Should().NotContainKey(WorkflowRunCommandMetadataKeys.SessionId);
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.FireAtUtc].Should().Be(firstScheduledFireAt.ToUniversalTime().ToString("O"));
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        chatRequest.Metadata.Should().NotContainKey("workflow.schedule_id");
        chatRequest.Metadata.Should().NotContainKey("workflow.scheduled_fire_at_utc");
        chatRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.IdempotencyKey);

        agent.State.FireCount.Should().Be(1);
        agent.State.FireRecords.Should().ContainKey(idempotencyKey);
        var fireRecord = agent.State.FireRecords[idempotencyKey];
        fireRecord.Manual.Should().BeFalse();
        fireRecord.Status.Should().Be(ScheduledDispatchFireStatusState.Dispatched);

        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].ActorId.Should().Be(ScheduleActorId);
        scheduler.Canceled[0].CallbackId.Should().Be(NextFireCallbackId);
        scheduler.Canceled[0].Generation.Should().Be(1);

        scheduler.TimeoutRequests.Should().HaveCount(2);
        var nextRequest = scheduler.TimeoutRequests[1];
        var nextFireCommand = nextRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        nextFireCommand.Manual.Should().BeFalse();
        nextFireCommand.ScheduledFireAt.ToDateTimeOffset().Should().BeAfter(firstScheduledFireAt);
        agent.State.NextFireLease!.Generation.Should().Be(2);
    }

    [Fact]
    public async Task HandleFireAsync_ShouldPreserveWorkflowAdapterMetadataWithoutCoreWorkflowLeak()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            triggerEnvelope: CreateTriggerEnvelope("target-actor-1", new ChatRequestEvent
            {
                Prompt = "hello",
                Metadata =
                {
                    ["workflow.schedule_id"] = "schedule-1",
                },
            }),
            enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        var chatRequest = dispatch.Dispatches.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
        chatRequest.Metadata["workflow.schedule_id"].Should().Be("schedule-1");
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.FireAtUtc].Should().Be(scheduledFireAt.ToUniversalTime().ToString("O"));
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        chatRequest.Metadata.Should().NotContainKey("workflow.scheduled_fire_at_utc");
        chatRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.IdempotencyKey);
    }

    [Fact]
    public async Task HandleFireAsync_ShouldDispatchStoredEnvelopeToConfiguredNonWorkflowTarget()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            targetActorId: "generic-agent-1",
            triggerEnvelope: CreateTriggerEnvelope("generic-agent-1", new ChatRequestEvent
            {
                Prompt = "generic scheduled prompt",
            }),
            enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("generic-agent-1");
        dispatched.Envelope.Route.GetTargetActorId().Should().Be("generic-agent-1");
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        dispatched.Envelope.Id.Should().Be(idempotencyKey);
        var chatRequest = dispatched.Envelope.Payload.Unpack<ChatRequestEvent>();
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.FireAtUtc].Should().Be(scheduledFireAt.ToUniversalTime().ToString("O"));
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        chatRequest.Metadata.Should().NotContainKey("workflow.schedule_id");
        chatRequest.Metadata.Should().NotContainKey("workflow.scheduled_fire_at_utc");
        chatRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.IdempotencyKey);
        agent.State.FireRecords[idempotencyKey].TargetActorId.Should().Be("generic-agent-1");
    }

    [Fact]
    public async Task HandleEventAsync_ShouldIgnoreStaleCallbackWithoutDispatchOrReschedule()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        var request = scheduler.TimeoutRequests.Single();

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(request, generation: 99, fireIndex: 1));

        dispatch.Dispatches.Should().BeEmpty();
        agent.State.FireRecords.Should().BeEmpty();
        scheduler.Canceled.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().ContainSingle();
        agent.State.NextFireLease!.Generation.Should().Be(1);
    }

    [Fact]
    public async Task HandleFireAsync_ShouldIgnoreDisabledNonManualFireWithoutLeaseEnvelope()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero)),
            Manual = false,
        });

        dispatch.Dispatches.Should().BeEmpty();
        agent.State.FireRecords.Should().BeEmpty();
        agent.State.FireCount.Should().Be(0);
        agent.State.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleFireAsync_ShouldRecordFailure_WhenDispatchIsNotAccepted()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort
        {
            AdmissionFactory = (_, envelope) => new DispatchAdmission(
                Accepted: false,
                CommandId: envelope.Id,
                AckedAt: DateTimeOffset.UtcNow,
                ActorId: string.Empty,
                CorrelationId: envelope.Propagation?.CorrelationId ?? envelope.Id),
        };
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        dispatch.Dispatches.Should().ContainSingle();
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.LastError.Should().Be("Scheduled dispatch was not accepted.");
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Failed);
        agent.State.FireRecords[idempotencyKey].Error.Should().Be("Scheduled dispatch was not accepted.");
    }

    [Fact]
    public async Task HandleFireAsync_ShouldRecordFailure_WhenDispatchThrows()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort
        {
            DispatchException = new InvalidOperationException("dispatch unavailable"),
        };
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        dispatch.Dispatches.Should().ContainSingle();
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.LastError.Should().Be("dispatch unavailable");
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Failed);
        agent.State.FireRecords[idempotencyKey].Error.Should().Be("dispatch unavailable");
    }

    private static ScheduledDispatchGAgent CreateAgent(
        IEventStore eventStore,
        RecordingActorDispatchPort dispatch,
        RecordingRuntimeCallbackScheduler? callbackScheduler = null)
    {
        var agent = new ScheduledDispatchGAgent(dispatch)
        {
            Services = new TestServiceProvider(callbackScheduler),
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScheduledDispatchState>(eventStore),
        };
        SetAgentId(agent, ScheduleActorId);
        return agent;
    }

    private static EventEnvelope CreateFiredCallbackEnvelope(
        RuntimeCallbackTimeoutRequest request,
        long generation,
        long fireIndex)
    {
        var envelope = request.TriggerEnvelope.Clone();
        envelope.Id = Guid.NewGuid().ToString("N");
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        var callback = envelope.EnsureRuntime().EnsureCallback();
        callback.CallbackId = request.CallbackId;
        callback.Generation = generation;
        callback.FireIndex = fireIndex;
        callback.FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return envelope;
    }

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
    }

    private static ScheduledDispatchConfigureCommand CreateConfigureCommand(
        string scheduleId = "schedule-1",
        string targetActorId = "target-actor-1",
        string cronExpression = "*/15 * * * *",
        bool enabled = false,
        EventEnvelope? triggerEnvelope = null)
    {
        return new ScheduledDispatchConfigureCommand
        {
            ScheduleId = scheduleId,
            DisplayName = "Test schedule",
            TargetActorId = targetActorId,
            TriggerEnvelope = triggerEnvelope ?? CreateTriggerEnvelope(targetActorId, new ChatRequestEvent
            {
                Prompt = "hello",
                SessionId = "template-session",
            }),
            CronExpression = cronExpression,
            Timezone = "UTC",
            Enabled = enabled,
        };
    }

    private static EventEnvelope CreateTriggerEnvelope(string targetActorId, IMessage payload) =>
        new()
        {
            Id = "template-command",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("schedule-template", targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "template-correlation",
            },
        };

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Func<string, EventEnvelope, DispatchAdmission> AdmissionFactory { get; set; } =
            DispatchAdmissionFactory.Create;

        public Exception? DispatchException { get; set; }

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope.Clone()));
            if (DispatchException != null)
                throw DispatchException;

            return Task.FromResult(AdmissionFactory(actorId, envelope));
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private readonly Dictionary<(string ActorId, string CallbackId), long> _generations = [];

        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutRequests.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                DueTime = request.DueTime,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DeliveryMode = request.DeliveryMode,
            });

            var key = (request.ActorId, request.CallbackId);
            var generation = _generations.GetValueOrDefault(key) + 1;
            _generations[key] = generation;
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                generation,
                RuntimeCallbackBackend.Dedicated));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            throw new NotSupportedException("Scheduled dispatch tests only use one-shot durable timeouts.");
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestServiceProvider(RecordingRuntimeCallbackScheduler? callbackScheduler) : IServiceProvider
    {
        public object? GetService(System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return callbackScheduler;

            return null;
        }
    }

    private sealed class TestEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _streams = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            currentVersion.Should().Be(expectedVersion);

            var committed = events.Select(x => x.Clone()).ToList();
            stream.AddRange(committed);
            _streams[agentId] = stream;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { committed },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult<IReadOnlyList<StateEvent>>(
                events.Where(x => !fromVersion.HasValue || x.Version >= fromVersion.Value)
                    .Select(x => x.Clone())
                    .ToArray());
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult(stream.Count == 0 ? 0 : stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0L);
        }
    }

}
