using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Aevatar.Interop.A2A.Application;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Interop.A2A.Tests;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: tests verified lifecycle mutations on an application-level task store.
//   New principle: task actor tests verify committed events, replayable state, and update publication.
public class A2ATaskGAgentTests
{
    [Fact]
    public async Task HandleSubmitAsync_CommitsSubmittedStateAndPublishesUpdate()
    {
        var harness = new TaskAgentHarness("a2a-task:task-1");
        await harness.Agent.ActivateAsync();

        await harness.Agent.HandleEventAsync(Envelope(new A2ATaskSubmitCommand
        {
            TaskId = "task-1",
            SessionId = "session-1",
            TargetActorId = "target-1",
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            Message = A2ATaskModelMapper.ToProto(MakeMessage("hello")),
            Metadata = { ["agentId"] = "target-1" },
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        harness.Agent.State.TaskId.Should().Be("task-1");
        harness.Agent.State.Status.State.Should().Be(A2ATaskLifecycleState.Submitted);
        harness.Agent.State.History.Should().ContainSingle();
        harness.Agent.State.Metadata["agentId"].Should().Be("target-1");
        var storeEvents = await harness.GetStoreEventsAsync();
        storeEvents.Should().ContainSingle();
        storeEvents[0].EventData.Unpack<A2ATaskSubmittedEvent>().State.TaskId.Should().Be("task-1");
        var update = harness.Publisher.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<A2ATaskUpdate>().Subject;
        update.ActorId.Should().Be("a2a-task:task-1");
        update.TaskId.Should().Be("task-1");
        update.IsFinal.Should().BeFalse();
    }

    [Fact]
    public async Task HandleSubmitAsync_WhenAlreadySubmitted_DoesNotCommitDuplicateEvent()
    {
        var harness = new TaskAgentHarness("a2a-task:task-duplicate");
        await harness.Agent.ActivateAsync();
        var command = new A2ATaskSubmitCommand
        {
            TaskId = "task-duplicate",
            TargetActorId = "target-1",
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            Message = A2ATaskModelMapper.ToProto(MakeMessage("hello")),
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        await harness.Agent.HandleEventAsync(Envelope(command));
        await harness.Agent.HandleEventAsync(Envelope(command.Clone()));

        var storeEvents = await harness.GetStoreEventsAsync();
        storeEvents.Should().ContainSingle();
        harness.Agent.State.StateVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleCancelAsync_WhenSubmitted_CommitsCanceledStateAndFinalUpdate()
    {
        var harness = new TaskAgentHarness("a2a-task:task-cancel");
        await harness.Agent.ActivateAsync();
        await harness.Agent.HandleEventAsync(Envelope(new A2ATaskSubmitCommand
        {
            TaskId = "task-cancel",
            TargetActorId = "target-1",
            CommandId = "cmd-submit",
            CorrelationId = "corr-submit",
            Message = A2ATaskModelMapper.ToProto(MakeMessage("hello")),
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        await harness.Agent.HandleEventAsync(Envelope(new A2ATaskCancelCommand
        {
            TaskId = "task-cancel",
            CommandId = "cmd-cancel",
            CorrelationId = "corr-cancel",
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        harness.Agent.State.Status.State.Should().Be(A2ATaskLifecycleState.Canceled);
        harness.Agent.State.CommandId.Should().Be("cmd-cancel");
        harness.Agent.State.StateVersion.Should().Be(2);
        var storeEvents = await harness.GetStoreEventsAsync();
        storeEvents.Should().HaveCount(2);
        storeEvents[^1].EventData.Unpack<A2ATaskCancelSubmittedEvent>().State.Status.State
            .Should().Be(A2ATaskLifecycleState.Canceled);
        var update = harness.Publisher.Published[^1].Event.Should().BeOfType<A2ATaskUpdate>().Subject;
        update.IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task HandleCancelAsync_WhenTaskMissingOrFinal_DoesNotCommitEvent()
    {
        var missing = new TaskAgentHarness("a2a-task:missing");
        await missing.Agent.ActivateAsync();

        await missing.Agent.HandleEventAsync(Envelope(new A2ATaskCancelCommand
        {
            TaskId = "missing",
            CommandId = "cmd-cancel",
            CorrelationId = "corr-cancel",
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        var missingEvents = await missing.GetStoreEventsAsync();
        missingEvents.Should().BeEmpty();

        var final = new TaskAgentHarness("a2a-task:final");
        await final.Agent.ActivateAsync();
        await final.Agent.HandleEventAsync(Envelope(new A2ATaskSubmitCommand
        {
            TaskId = "final",
            TargetActorId = "target-1",
            CommandId = "cmd-submit",
            CorrelationId = "corr-submit",
            Message = A2ATaskModelMapper.ToProto(MakeMessage("hello")),
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));
        await final.Agent.HandleEventAsync(Envelope(new A2ATaskCancelCommand
        {
            TaskId = "final",
            CommandId = "cmd-cancel-1",
            CorrelationId = "corr-cancel-1",
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));
        await final.Agent.HandleEventAsync(Envelope(new A2ATaskCancelCommand
        {
            TaskId = "final",
            CommandId = "cmd-cancel-2",
            CorrelationId = "corr-cancel-2",
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        var finalEvents = await final.GetStoreEventsAsync();
        finalEvents.Should().HaveCount(2);
        final.Agent.State.CommandId.Should().Be("cmd-cancel-1");
    }

    private static Message MakeMessage(string text) =>
        new()
        {
            Role = "user",
            Parts = [new TextPart { Text = text }],
        };

    private static EventEnvelope Envelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
        };

    private sealed class TaskAgentHarness
    {
        private readonly RecordingEventStore _store = new();

        public TaskAgentHarness(string actorId)
        {
            Publisher = new RecordingPublisher();
            var services = new ServiceCollection()
                .AddSingleton<IEventStore>(_store)
                .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
                .AddSingleton(new EventSourcingRuntimeOptions { EnableSnapshots = false })
                .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
                .BuildServiceProvider();

            Agent = new A2ATaskGAgent
            {
                Services = services,
                EventPublisher = Publisher,
                EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<A2ATaskState>>(),
            };
            typeof(Aevatar.Foundation.Core.GAgentBase)
                .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Agent, [actorId]);
        }

        public A2ATaskGAgent Agent { get; }

        public RecordingPublisher Publisher { get; }

        public Task<IReadOnlyList<StateEvent>> GetStoreEventsAsync() =>
            _store.GetEventsAsync(Agent.Id);
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<(IMessage Event, TopologyAudience Audience)> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            _ = evt;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private readonly List<StateEvent> _events = [];

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var currentVersion = _events.Count == 0 ? 0 : _events[^1].Version;
            if (currentVersion != expectedVersion)
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, currentVersion);

            var committed = events.Select(static evt => evt.Clone()).ToArray();
            _events.AddRange(committed);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = _events.Count == 0 ? currentVersion : _events[^1].Version,
                CommittedEvents = { committed },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            _ = agentId;
            ct.ThrowIfCancellationRequested();
            var result = _events
                .Where(evt => !fromVersion.HasValue || evt.Version > fromVersion.Value)
                .Select(static evt => evt.Clone())
                .ToList();
            return Task.FromResult<IReadOnlyList<StateEvent>>(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            _ = agentId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_events.Count == 0 ? 0 : _events[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            _ = agentId;
            ct.ThrowIfCancellationRequested();
            var removed = _events.RemoveAll(evt => evt.Version <= toVersion);
            return Task.FromResult((long)removed);
        }
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
