using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowLeaseGAgentTests
{
    private const string LeaseActorId = "workflow.lease:test";

    [Fact]
    public async Task HandleAcquireAsync_WhenIdle_ShouldGrantTokenAndGeneration()
    {
        var sender = new RecordingSender();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(sender, scheduler);
        await agent.ActivateAsync();

        await agent.HandleAcquireAsync(Acquire("req-1", "run-1"), 1_000, CancellationToken.None);

        var granted = sender.Sent.Should().ContainSingle().Subject.Event
            .Should().BeOfType<WorkflowLeaseAcquiredEvent>().Subject;
        granted.HolderToken.Should().NotBeNullOrWhiteSpace();
        granted.Generation.Should().Be(1);
        granted.ExpiresAtUnixMs.Should().Be(301_000);
        agent.State.HolderRunId.Should().Be("run-1");
        agent.State.Generation.Should().Be(1);
        scheduler.TimeoutRequests.Should().ContainSingle();
        scheduler.TimeoutRequests[0].CallbackId.Should().Contain("workflow-lease-expiration");
    }

    [Fact]
    public async Task HandleAcquireAsync_WhenBusyAndFailPolicy_ShouldReject()
    {
        var sender = new RecordingSender();
        var agent = CreateAgent(sender, new RecordingRuntimeCallbackScheduler());
        await agent.ActivateAsync();
        await agent.HandleAcquireAsync(Acquire("req-1", "run-1"), 1_000, CancellationToken.None);
        sender.Sent.Clear();

        await agent.HandleAcquireAsync(Acquire("req-2", "run-2"), 2_000, CancellationToken.None);

        var rejected = sender.Sent.Should().ContainSingle().Subject.Event
            .Should().BeOfType<WorkflowLeaseRejectedEvent>().Subject;
        rejected.Reason.Should().Be(WorkflowLeaseRejectionReason.LeaseBusy);
        rejected.CurrentHolderRunId.Should().Be("run-1");
        agent.State.HolderRunId.Should().Be("run-1");
    }

    [Fact]
    public async Task HandleAcquireAsync_WhenBusyAndWaitPolicy_ShouldQueueUntilReleaseThenGrantFifo()
    {
        var sender = new RecordingSender();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(sender, scheduler);
        await agent.ActivateAsync();
        await agent.HandleAcquireAsync(Acquire("req-1", "run-1"), 1_000, CancellationToken.None);
        var firstGrant = sender.Sent.Select(x => x.Event).OfType<WorkflowLeaseAcquiredEvent>().Single();
        sender.Sent.Clear();

        await agent.HandleAcquireAsync(Acquire("req-2", "run-2", WorkflowLeaseConflictPolicy.Wait), 2_000, CancellationToken.None);
        await agent.HandleAcquireAsync(Acquire("req-3", "run-3", WorkflowLeaseConflictPolicy.Wait), 3_000, CancellationToken.None);

        agent.State.Waiters.Select(x => x.RequestId).Should().Equal("req-2", "req-3");
        sender.Sent.Should().BeEmpty();

        await agent.HandleReleaseAsync(new WorkflowLeaseReleaseRequestedEvent
        {
            LeaseKey = "shared",
            RequestId = "rel-1",
            RequesterRunId = "run-1",
            RequesterActorId = "actor-run-1",
            RequesterStepId = "release-step",
            HolderToken = firstGrant.HolderToken,
            Generation = firstGrant.Generation,
        }, CancellationToken.None);

        sender.Sent.Select(x => x.Event).OfType<WorkflowLeaseReleasedEvent>().Should().ContainSingle();
        var secondGrant = sender.Sent.Select(x => x.Event).OfType<WorkflowLeaseAcquiredEvent>().Single();
        secondGrant.RequestId.Should().Be("req-2");
        secondGrant.RequesterRunId.Should().Be("run-2");
        agent.State.HolderRunId.Should().Be("run-2");
        agent.State.Generation.Should().Be(2);
        agent.State.Waiters.Select(x => x.RequestId).Should().Equal("req-3");
        scheduler.Canceled.Should().Contain(x => x.CallbackId.Contains("workflow-lease-wait-timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAcquireAsync_WhenWaitQueueFull_ShouldReject()
    {
        var sender = new RecordingSender();
        var agent = CreateAgent(sender, new RecordingRuntimeCallbackScheduler());
        await agent.ActivateAsync();
        await agent.HandleAcquireAsync(Acquire("holder", "run-holder"), 1_000, CancellationToken.None);
        sender.Sent.Clear();

        for (var i = 0; i < WorkflowLeaseGAgent.MaxWaiters; i++)
        {
            await agent.HandleAcquireAsync(
                Acquire($"wait-{i}", $"run-wait-{i}", WorkflowLeaseConflictPolicy.Wait),
                2_000 + i,
                CancellationToken.None);
        }

        await agent.HandleAcquireAsync(
            Acquire("overflow", "run-overflow", WorkflowLeaseConflictPolicy.Wait),
            5_000,
            CancellationToken.None);

        var rejected = sender.Sent.Select(x => x.Event).OfType<WorkflowLeaseRejectedEvent>().Single();
        rejected.Reason.Should().Be(WorkflowLeaseRejectionReason.WaitQueueFull);
        agent.State.Waiters.Should().HaveCount(WorkflowLeaseGAgent.MaxWaiters);
    }

    [Fact]
    public async Task HandleWaitTimeoutFiredAsync_ShouldRemoveWaiterAndReject()
    {
        var sender = new RecordingSender();
        var agent = CreateAgent(sender, new RecordingRuntimeCallbackScheduler());
        await agent.ActivateAsync();
        await agent.HandleAcquireAsync(Acquire("holder", "run-holder"), 1_000, CancellationToken.None);
        await agent.HandleAcquireAsync(Acquire("wait-1", "run-wait", WorkflowLeaseConflictPolicy.Wait), 2_000, CancellationToken.None);
        sender.Sent.Clear();

        await agent.HandleWaitTimeoutFiredAsync(new WorkflowLeaseWaitTimeoutFiredEvent
        {
            LeaseKey = "shared",
            RequestId = "wait-1",
            RequesterRunId = "run-wait",
            RequesterActorId = "actor-run-wait",
            RequesterStepId = "step-wait",
        }, CancellationToken.None);

        agent.State.Waiters.Should().BeEmpty();
        var rejected = sender.Sent.Should().ContainSingle().Subject.Event
            .Should().BeOfType<WorkflowLeaseRejectedEvent>().Subject;
        rejected.Reason.Should().Be(WorkflowLeaseRejectionReason.WaitTimeout);
    }

    [Fact]
    public async Task HandleExpirationFiredAsync_ShouldFenceStaleCallbacksAndGrantCurrentWaiter()
    {
        var sender = new RecordingSender();
        var agent = CreateAgent(sender, new RecordingRuntimeCallbackScheduler());
        await agent.ActivateAsync();
        await agent.HandleAcquireAsync(Acquire("holder", "run-holder"), 1_000, CancellationToken.None);
        var holder = sender.Sent.Select(x => x.Event).OfType<WorkflowLeaseAcquiredEvent>().Single();
        await agent.HandleAcquireAsync(Acquire("wait-1", "run-wait", WorkflowLeaseConflictPolicy.Wait), 2_000, CancellationToken.None);
        sender.Sent.Clear();

        await agent.HandleExpirationFiredAsync(new WorkflowLeaseExpirationFiredEvent
        {
            LeaseKey = "shared",
            HolderToken = "stale",
            Generation = holder.Generation,
            ExpiresAtUnixMs = holder.ExpiresAtUnixMs,
        }, 400_000, CancellationToken.None);

        agent.State.HolderRunId.Should().Be("run-holder");
        sender.Sent.Should().BeEmpty();

        await agent.HandleExpirationFiredAsync(new WorkflowLeaseExpirationFiredEvent
        {
            LeaseKey = "shared",
            HolderToken = holder.HolderToken,
            Generation = holder.Generation,
            ExpiresAtUnixMs = holder.ExpiresAtUnixMs,
        }, 400_000, CancellationToken.None);

        var granted = sender.Sent.Should().ContainSingle().Subject.Event
            .Should().BeOfType<WorkflowLeaseAcquiredEvent>().Subject;
        granted.RequestId.Should().Be("wait-1");
        agent.State.HolderRunId.Should().Be("run-wait");
    }

    [Fact]
    public async Task HandleRenewAsync_ShouldExtendExpiryWithoutGenerationBumpAndFenceStaleRelease()
    {
        var sender = new RecordingSender();
        var agent = CreateAgent(sender, new RecordingRuntimeCallbackScheduler());
        await agent.ActivateAsync();
        await agent.HandleAcquireAsync(Acquire("holder", "run-holder"), 1_000, CancellationToken.None);
        var holder = sender.Sent.Select(x => x.Event).OfType<WorkflowLeaseAcquiredEvent>().Single();
        sender.Sent.Clear();

        await agent.HandleRenewAsync(new WorkflowLeaseRenewRequestedEvent
        {
            LeaseKey = "shared",
            RequestId = "renew-1",
            RequesterRunId = "run-holder",
            RequesterActorId = "actor-run-holder",
            RequesterStepId = "renew-step",
            HolderToken = holder.HolderToken,
            Generation = holder.Generation,
            TtlMs = 60_000,
        }, 10_000, CancellationToken.None);

        var renewed = sender.Sent.Should().ContainSingle().Subject.Event
            .Should().BeOfType<WorkflowLeaseRenewedEvent>().Subject;
        renewed.Generation.Should().Be(holder.Generation);
        renewed.ExpiresAtUnixMs.Should().Be(70_000);
        agent.State.Generation.Should().Be(holder.Generation);
        sender.Sent.Clear();

        await agent.HandleReleaseAsync(new WorkflowLeaseReleaseRequestedEvent
        {
            LeaseKey = "shared",
            RequestId = "release-stale",
            RequesterRunId = "run-holder",
            RequesterActorId = "actor-run-holder",
            RequesterStepId = "release-step",
            HolderToken = "stale-token",
            Generation = holder.Generation,
        }, CancellationToken.None);

        var rejected = sender.Sent.Should().ContainSingle().Subject.Event
            .Should().BeOfType<WorkflowLeaseRejectedEvent>().Subject;
        rejected.Reason.Should().Be(WorkflowLeaseRejectionReason.StaleHolder);
        agent.State.HolderToken.Should().Be(holder.HolderToken);
    }

    private static WorkflowLeaseAcquireRequestedEvent Acquire(
        string requestId,
        string runId,
        WorkflowLeaseConflictPolicy onConflict = WorkflowLeaseConflictPolicy.Fail) =>
        new()
        {
            LeaseKey = "shared",
            RequestId = requestId,
            RequesterRunId = runId,
            RequesterActorId = $"actor-{runId}",
            RequesterStepId = $"step-{requestId}",
            TtlMs = WorkflowLeaseGAgent.DefaultLeaseTtlMs,
            WaitTimeoutMs = WorkflowLeaseGAgent.DefaultWaitTimeoutMs,
            OnConflict = onConflict,
        };

    private static WorkflowLeaseGAgent CreateAgent(
        RecordingSender sender,
        RecordingRuntimeCallbackScheduler scheduler)
    {
        var agent = new WorkflowLeaseGAgent
        {
            Services = new TestServiceProvider(scheduler),
            EventPublisher = sender,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowLeaseState>(new TestEventStore()),
        };
        SetAgentId(agent, LeaseActorId);
        return agent;
    }

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
    }

    private sealed class RecordingSender : IEventPublisher
    {
        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public Task PublishAsync<T>(
            T evt,
            TopologyAudience audience,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage
        {
            Sent.Add((targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private long _generation;

        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            TimeoutRequests.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                DueTime = request.DueTime,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DeliveryMode = request.DeliveryMode,
            });
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Interlocked.Increment(ref _generation),
                RuntimeCallbackBackend.Dedicated));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            Canceled.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TestServiceProvider(RecordingRuntimeCallbackScheduler scheduler) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return scheduler;

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
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult<IReadOnlyList<StateEvent>>(
                events.Where(x => !fromVersion.HasValue || x.Version >= fromVersion.Value)
                    .Select(x => x.Clone())
                    .ToArray());
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult(stream.Count == 0 ? 0 : stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            Task.FromResult(0L);
    }
}
