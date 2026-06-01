using System.Reflection;
using Aevatar.CQRS.Core.Abstractions.Commands;
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

public sealed class WorkflowScheduleGAgentTests
{
    private const string ScheduleActorId = "workflow-schedule:schedule-1";
    private const string NextFireCallbackId = "workflow-schedule-next-fire";

    [Fact]
    public async Task HandleFireAsync_ShouldSuppressDuplicateDispatchAfterStartedRecordIsDurable()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingWorkflowRunDispatchService();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(new WorkflowScheduleConfigureCommand
        {
            ScheduleId = "schedule-1",
            WorkflowName = "direct",
            Prompt = "hello",
            CronExpression = "*/15 * * * *",
            Timezone = "UTC",
            Enabled = false,
        });

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new WorkflowScheduleFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });
        await agent.HandleFireAsync(new WorkflowScheduleFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        dispatch.Commands.Should().ContainSingle();
        var idempotencyKey = WorkflowScheduleCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        agent.State.FireRecords.Should().ContainKey(idempotencyKey);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(WorkflowScheduleFireStatusState.Dispatched);
    }

    [Fact]
    public async Task HandleConfigureAsync_WhenEnabled_ShouldRegisterDurableNextFireCallback()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingWorkflowRunDispatchService();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();

        await agent.HandleConfigureAsync(new WorkflowScheduleConfigureCommand
        {
            ScheduleId = "schedule-1",
            WorkflowName = "direct",
            Prompt = "hello",
            CronExpression = "* * * * *",
            Timezone = "UTC",
            Enabled = true,
        });

        scheduler.TimeoutRequests.Should().ContainSingle();
        var request = scheduler.TimeoutRequests[0];
        request.ActorId.Should().Be(ScheduleActorId);
        request.CallbackId.Should().Be(NextFireCallbackId);
        request.DeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.FiredSelfEvent);
        request.DueTime.Should().BePositive();
        request.DueTime.Should().BeLessThan(TimeSpan.FromSeconds(70));

        var fireCommand = request.TriggerEnvelope.Payload.Unpack<WorkflowScheduleFireCommand>();
        fireCommand.Manual.Should().BeFalse();
        fireCommand.ScheduledFireAt.Should().NotBeNull();
        var scheduledFireAt = fireCommand.ScheduledFireAt.ToDateTimeOffset();
        scheduledFireAt.Should().Be(agent.State.NextFireAt);

        agent.State.NextFireLease.Should().NotBeNull();
        agent.State.NextFireLease!.ActorId.Should().Be(ScheduleActorId);
        agent.State.NextFireLease.CallbackId.Should().Be(NextFireCallbackId);
        agent.State.NextFireLease.Generation.Should().Be(1);
        agent.State.NextFireLease.Backend.Should().Be(WorkflowScheduleRuntimeCallbackBackendState.Dedicated);
    }

    [Fact]
    public async Task HandleDisableAsync_ShouldCancelExistingLeaseBeforeDisabledStateClearsIt()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingWorkflowRunDispatchService();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(new WorkflowScheduleConfigureCommand
        {
            ScheduleId = "schedule-1",
            WorkflowName = "direct",
            Prompt = "hello",
            CronExpression = "* * * * *",
            Timezone = "UTC",
            Enabled = true,
        });

        await agent.HandleDisableAsync(new WorkflowScheduleDisableCommand
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
        var dispatch = new RecordingWorkflowRunDispatchService();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(new WorkflowScheduleConfigureCommand
        {
            ScheduleId = "schedule-1",
            WorkflowName = "direct",
            Prompt = "hello",
            CronExpression = "* * * * *",
            Timezone = "UTC",
            Enabled = true,
        });

        await agent.HandleConfigureAsync(new WorkflowScheduleConfigureCommand
        {
            ScheduleId = "schedule-1",
            WorkflowName = "direct",
            Prompt = "updated",
            CronExpression = "*/5 * * * *",
            Timezone = "UTC",
            Enabled = false,
        });

        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].ActorId.Should().Be(ScheduleActorId);
        scheduler.Canceled[0].CallbackId.Should().Be(NextFireCallbackId);
        scheduler.Canceled[0].Generation.Should().Be(1);
        scheduler.TimeoutRequests.Should().ContainSingle();
        agent.State.Enabled.Should().BeFalse();
        agent.State.Prompt.Should().Be("updated");
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
    }

    [Fact]
    public async Task HandleEventAsync_WhenDueCallbackArrives_ShouldDispatchNonManualFireAndScheduleNext()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingWorkflowRunDispatchService();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(new WorkflowScheduleConfigureCommand
        {
            ScheduleId = "schedule-1",
            WorkflowName = "direct",
            Prompt = "hello",
            CronExpression = "* * * * *",
            Timezone = "UTC",
            Enabled = true,
        });

        var firstRequest = scheduler.TimeoutRequests.Single();
        var firstFireCommand = firstRequest.TriggerEnvelope.Payload.Unpack<WorkflowScheduleFireCommand>();
        var firstScheduledFireAt = firstFireCommand.ScheduledFireAt.ToDateTimeOffset();

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(firstRequest, generation: 1, fireIndex: 1));

        dispatch.Commands.Should().ContainSingle();
        var dispatched = dispatch.Commands[0];
        dispatched.Metadata.Should().NotBeNull();
        dispatched.Metadata!["workflow.schedule_id"].Should().Be("schedule-1");
        dispatched.Metadata["workflow.scheduled_fire_at_utc"].Should().Be(firstScheduledFireAt.ToUniversalTime().ToString("O"));

        var idempotencyKey = WorkflowScheduleCalculator.BuildIdempotencyKey("schedule-1", firstScheduledFireAt);
        dispatched.SessionId.Should().Be(idempotencyKey);
        dispatched.Metadata[WorkflowRunCommandMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);

        agent.State.FireCount.Should().Be(1);
        agent.State.FireRecords.Should().ContainKey(idempotencyKey);
        var fireRecord = agent.State.FireRecords[idempotencyKey];
        fireRecord.Manual.Should().BeFalse();
        fireRecord.Status.Should().Be(WorkflowScheduleFireStatusState.Dispatched);

        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].ActorId.Should().Be(ScheduleActorId);
        scheduler.Canceled[0].CallbackId.Should().Be(NextFireCallbackId);
        scheduler.Canceled[0].Generation.Should().Be(1);

        scheduler.TimeoutRequests.Should().HaveCount(2);
        var nextRequest = scheduler.TimeoutRequests[1];
        var nextFireCommand = nextRequest.TriggerEnvelope.Payload.Unpack<WorkflowScheduleFireCommand>();
        nextFireCommand.Manual.Should().BeFalse();
        nextFireCommand.ScheduledFireAt.ToDateTimeOffset().Should().BeAfter(firstScheduledFireAt);
        agent.State.NextFireLease!.Generation.Should().Be(2);
    }

    private static WorkflowScheduleGAgent CreateAgent(
        IEventStore eventStore,
        RecordingWorkflowRunDispatchService dispatch,
        RecordingRuntimeCallbackScheduler? callbackScheduler = null)
    {
        var agent = new WorkflowScheduleGAgent(dispatch)
        {
            Services = new TestServiceProvider(callbackScheduler),
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowScheduleState>(eventStore),
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

    private sealed class RecordingWorkflowRunDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public List<WorkflowChatRunRequest> Commands { get; } = [];

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(
                CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                    new WorkflowChatRunAcceptedReceipt("run-actor-1", "direct", "cmd-1", "corr-1")));
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
            throw new NotSupportedException("Workflow schedule tests only use one-shot durable timeouts.");
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
