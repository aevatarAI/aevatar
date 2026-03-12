using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptRuntimeGAgentEventDrivenQueryTests
{
    [Fact]
    public async Task HandleRunScriptRequested_ShouldThrow_WhenDefinitionActorIdIsMissing()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        var act = () => agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-missing-definition",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-missing-definition",
            DefinitionActorId = string.Empty,
            RequestedEventType = "chat.requested",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DefinitionActorId is required*");
    }

    [Fact]
    public async Task EventDrivenQuery_ShouldExecuteRun_WhenSnapshotResponseMatchesPendingRequest()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-1",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-1",
            DefinitionActorId = "definition-actor-1",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-actor-1")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();
        var scheduled = scheduler.Timeouts.Should().ContainSingle().Subject;

        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeTrue();
        scheduled.Lease.CallbackId.Should().Be(
            RuntimeCallbackKeyComposer.BuildCallbackId("script-definition-query-timeout", query.RequestId));

        await agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = query.RequestId,
            Found = true,
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = "public sealed class RuntimeScript {}",
            ReadModelSchemaVersion = "v1",
            ReadModelSchemaHash = "hash-v1",
        });

        orchestrator.Requests.Should().ContainSingle();
        agent.State.LastRunId.Should().Be("run-1");
        agent.State.Revision.Should().Be("rev-1");
        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeFalse();
        scheduler.Canceled.Should().ContainSingle(x => x.CallbackId == scheduled.Lease.CallbackId);

        await agent.HandleEventAsync(CreateTimeoutEnvelope(agent.Id, scheduled.Lease, query.RequestId, "run-1"));

        orchestrator.Requests.Should().ContainSingle();
        agent.State.LastRunId.Should().Be("run-1");
    }

    [Fact]
    public async Task TimeoutSignal_ShouldPersistFailure_AndIgnoreLateSnapshotResponse()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-2",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-2",
            DefinitionActorId = "definition-actor-2",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-actor-2")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();
        var scheduled = scheduler.Timeouts.Should().ContainSingle().Subject;

        await agent.HandleEventAsync(CreateTimeoutEnvelope(agent.Id, scheduled.Lease, query.RequestId, "run-2"));

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastRunId.Should().Be("run-2");
        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeFalse();
        var versionAfterTimeout = agent.State.LastAppliedEventVersion;

        await agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = query.RequestId,
            Found = true,
            ScriptId = "script-2",
            Revision = "rev-2",
            SourceText = "public sealed class RuntimeScript2 {}",
            ReadModelSchemaVersion = "v2",
            ReadModelSchemaHash = "hash-v2",
        });

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastAppliedEventVersion.Should().Be(versionAfterTimeout);
    }

    [Fact]
    public async Task PendingQuery_ShouldSurviveReplay_AndLateSnapshotResponseShouldBeProcessed()
    {
        var eventStore = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(orchestrator, publisher, scheduler, eventStore);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-replay-1",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-replay-1",
            DefinitionActorId = "definition-actor-replay-1",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-actor-replay-1")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();

        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeTrue();

        var replayed = CreateAgent(
            new RecordingOrchestrator(),
            new RecordingEventPublisher(),
            scheduler,
            eventStore);
        SetAgentId(replayed, agent.Id);
        await replayed.ActivateAsync();

        HasPendingDefinitionQuery(replayed, query.RequestId).Should().BeTrue();
        scheduler.Timeouts.Should().HaveCount(2);

        await replayed.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = query.RequestId,
            Found = true,
            ScriptId = "script-replay-1",
            Revision = "rev-replay-1",
            SourceText = "public sealed class RuntimeReplayScript {}",
            ReadModelSchemaVersion = "v-replay-1",
            ReadModelSchemaHash = "hash-replay-1",
        });

        replayed.State.LastRunId.Should().Be("run-replay-1");
        HasPendingDefinitionQuery(replayed, query.RequestId).Should().BeFalse();
    }

    [Fact]
    public async Task PendingQueryTimeout_ShouldSurviveReplay_AndIgnoreStaleLease()
    {
        var eventStore = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(orchestrator, publisher, scheduler, eventStore);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-replay-timeout",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-replay-timeout",
            DefinitionActorId = "definition-actor-replay-timeout",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-actor-replay-timeout")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();
        var originalLease = scheduler.Timeouts.Should().ContainSingle().Subject.Lease;

        var replayed = CreateAgent(
            new RecordingOrchestrator(),
            new RecordingEventPublisher(),
            scheduler,
            eventStore);
        SetAgentId(replayed, agent.Id);
        await replayed.ActivateAsync();

        var recoveredLease = scheduler.Timeouts.Last().Lease;
        recoveredLease.Generation.Should().BeGreaterThan(originalLease.Generation);
        HasPendingDefinitionQuery(replayed, query.RequestId).Should().BeTrue();

        await replayed.HandleEventAsync(CreateTimeoutEnvelope(
            replayed.Id,
            originalLease,
            query.RequestId,
            "run-replay-timeout"));

        replayed.State.LastRunId.Should().BeEmpty();
        HasPendingDefinitionQuery(replayed, query.RequestId).Should().BeTrue();

        await replayed.HandleEventAsync(CreateTimeoutEnvelope(
            replayed.Id,
            recoveredLease,
            query.RequestId,
            "run-replay-timeout"));

        replayed.State.LastRunId.Should().Be("run-replay-timeout");
        HasPendingDefinitionQuery(replayed, query.RequestId).Should().BeFalse();
    }

    [Fact]
    public async Task RecoveredPendingQuery_ShouldScheduleOnlyRemainingTimeout()
    {
        var eventStore = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var agentId = "runtime-recover-remaining";
        var requestId = "request-recover-remaining";

        await SeedPendingDefinitionQueryAsync(
            eventStore,
            agentId,
            requestId,
            runId: "run-recover-remaining",
            definitionActorId: "definition-recover-remaining",
            scriptRevision: "rev-recover-remaining",
            queuedAtUnixTimeMs: DateTimeOffset.UtcNow.AddSeconds(-12).ToUnixTimeMilliseconds());

        var replayed = CreateAgent(orchestrator, publisher, scheduler, eventStore);
        SetAgentId(replayed, agentId);
        await replayed.ActivateAsync();

        var scheduled = scheduler.Timeouts.Should().ContainSingle().Subject.Request;
        scheduled.CallbackId.Should().Be(
            RuntimeCallbackKeyComposer.BuildCallbackId("script-definition-query-timeout", requestId));
        scheduled.DueTime.Should().BeLessThan(TimeSpan.FromSeconds(40));
        scheduled.DueTime.Should().BeGreaterThan(TimeSpan.FromSeconds(25));
        HasPendingDefinitionQuery(replayed, requestId).Should().BeTrue();
        publisher.Sent.Should().ContainSingle(x => x.TargetActorId == "definition-recover-remaining");
    }

    [Fact]
    public async Task RecoveredExpiredPendingQuery_ShouldFailImmediatelyWithoutRescheduling()
    {
        var eventStore = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        const string agentId = "runtime-recover-expired";
        const string requestId = "request-recover-expired";

        await SeedPendingDefinitionQueryAsync(
            eventStore,
            agentId,
            requestId,
            runId: "run-recover-expired",
            definitionActorId: "definition-recover-expired",
            scriptRevision: "rev-recover-expired",
            queuedAtUnixTimeMs: DateTimeOffset.UtcNow.AddSeconds(-50).ToUnixTimeMilliseconds());

        var replayed = CreateAgent(orchestrator, publisher, scheduler, eventStore);
        SetAgentId(replayed, agentId);
        await replayed.ActivateAsync();

        scheduler.Timeouts.Should().BeEmpty();
        publisher.Sent.Should().BeEmpty();
        orchestrator.Requests.Should().BeEmpty();
        replayed.State.LastRunId.Should().Be("run-recover-expired");
        HasPendingDefinitionQuery(replayed, requestId).Should().BeFalse();
    }

    [Fact]
    public async Task TimeoutSignal_WithMismatchedRunId_ShouldBeIgnored()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-3",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-3",
            DefinitionActorId = "definition-actor-3",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-actor-3")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();
        var scheduled = scheduler.Timeouts.Should().ContainSingle().Subject;

        await agent.HandleEventAsync(CreateTimeoutEnvelope(agent.Id, scheduled.Lease, query.RequestId, "another-run"));

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastRunId.Should().BeEmpty();
        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeTrue();

        await agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = query.RequestId,
            Found = true,
            ScriptId = "script-3",
            Revision = "rev-3",
            SourceText = "public sealed class RuntimeScript3 {}",
            ReadModelSchemaVersion = "v3",
            ReadModelSchemaHash = "hash-v3",
        });

        orchestrator.Requests.Should().ContainSingle();
        agent.State.LastRunId.Should().Be("run-3");
        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeFalse();
    }

    [Fact]
    public async Task TimeoutEnvelope_ShouldIgnoreMissingLeaseMetadata()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-no-metadata",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-no-metadata",
            DefinitionActorId = "definition-no-metadata",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-no-metadata")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();

        await agent.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ScriptDefinitionQueryTimeoutFiredEvent
            {
                RequestId = query.RequestId,
                RunId = "run-no-metadata",
            }),
            Route = new EnvelopeRoute
            {
                PublisherActorId = agent.Id,
                TargetActorId = agent.Id,
                Direction = EventDirection.Self,
            },
        });

        agent.State.LastRunId.Should().BeEmpty();
        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeTrue();
    }

    [Fact]
    public async Task TimeoutEnvelope_ShouldIgnoreStaleGeneration()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-stale-generation",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-stale-generation",
            DefinitionActorId = "definition-stale-generation",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-stale-generation")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();
        var scheduled = scheduler.Timeouts.Should().ContainSingle().Subject;

        await agent.HandleEventAsync(CreateTimeoutEnvelope(
            agent.Id,
            scheduled.Lease,
            query.RequestId,
            "run-stale-generation",
            generation: scheduled.Lease.Generation + 1));

        agent.State.LastRunId.Should().BeEmpty();
        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeTrue();
    }

    [Fact]
    public async Task EventDrivenQuery_ShouldPersistFailure_WhenQueryDispatchFails()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher
        {
            SendToException = new InvalidOperationException("dispatch-failed"),
        };
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-dispatch-failed",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-dispatch-failed",
            DefinitionActorId = "definition-dispatch-failed",
            RequestedEventType = "chat.requested",
        });

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastRunId.Should().Be("run-dispatch-failed");
        PendingDefinitionQueryCount(agent).Should().Be(0);
        scheduler.Canceled.Should().ContainSingle();
    }

    [Fact]
    public async Task SnapshotResponseNotFound_ShouldPersistFailure_AndClearPendingQuery()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-not-found",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-not-found",
            DefinitionActorId = "definition-not-found",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-not-found")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();

        await agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = query.RequestId,
            Found = false,
            FailureReason = "not-found",
        });

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastRunId.Should().Be("run-not-found");
        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeFalse();
    }

    [Fact]
    public async Task SnapshotResponseEmptySource_ShouldPersistFailure_AndClearPendingQuery()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-empty-source",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-empty-source",
            DefinitionActorId = "definition-empty-source",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-empty-source")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();

        await agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = query.RequestId,
            Found = true,
            ScriptId = "script-empty-source",
            Revision = "rev-empty-source",
            SourceText = string.Empty,
            ReadModelSchemaVersion = "v-empty-source",
            ReadModelSchemaHash = "hash-empty-source",
        });

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastRunId.Should().Be("run-empty-source");
        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeFalse();
    }

    [Fact]
    public async Task SnapshotResponseRevisionMismatch_ShouldPersistFailure_AndClearPendingQuery()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(orchestrator, publisher, scheduler);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-revision-mismatch",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-requested",
            DefinitionActorId = "definition-revision-mismatch",
            RequestedEventType = "chat.requested",
        });

        var query = publisher.Sent
            .Where(x => x.TargetActorId == "definition-revision-mismatch")
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();

        await agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = query.RequestId,
            Found = true,
            ScriptId = "script-revision-mismatch",
            Revision = "rev-actual",
            SourceText = "public sealed class RuntimeMismatchScript {}",
            ReadModelSchemaVersion = "v-revision-mismatch",
            ReadModelSchemaHash = "hash-revision-mismatch",
        });

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastRunId.Should().Be("run-revision-mismatch");
        HasPendingDefinitionQuery(agent, query.RequestId).Should().BeFalse();
    }

    private static ScriptRuntimeGAgent CreateAgent(
        RecordingOrchestrator orchestrator,
        RecordingEventPublisher publisher,
        RecordingCallbackScheduler scheduler,
        InMemoryEventStore? eventStore = null)
    {
        return new ScriptRuntimeGAgent(orchestrator)
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
                eventStore ?? new InMemoryEventStore()),
            Services = BuildServices(scheduler),
        };
    }

    private static ServiceProvider BuildServices(RecordingCallbackScheduler scheduler)
    {
        return new ServiceCollection()
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .BuildServiceProvider();
    }

    private static EventEnvelope CreateTimeoutEnvelope(
        string actorId,
        RuntimeCallbackLease lease,
        string requestId,
        string runId,
        long? generation = null)
    {
        return RuntimeCallbackEnvelopeFactory.CreateFiredEnvelope(
            actorId,
            lease.CallbackId,
            generation ?? lease.Generation,
            fireIndex: 0,
            triggerEnvelope: new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(new ScriptDefinitionQueryTimeoutFiredEvent
                {
                    RequestId = requestId,
                    RunId = runId,
                }),
                Route = new EnvelopeRoute
                {
                    PublisherActorId = actorId,
                    TargetActorId = actorId,
                    Direction = EventDirection.Self,
                },
            });
    }

    private static bool HasPendingDefinitionQuery(ScriptRuntimeGAgent agent, string requestId)
    {
        return agent.State.PendingDefinitionQueries.ContainsKey(requestId);
    }

    private static int PendingDefinitionQueryCount(ScriptRuntimeGAgent agent)
    {
        return agent.State.PendingDefinitionQueries.Count;
    }

    private static void SetAgentId(ScriptRuntimeGAgent agent, string agentId)
    {
        var method = typeof(Aevatar.Foundation.Core.GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(agent, [agentId]);
    }

    private static async Task SeedPendingDefinitionQueryAsync(
        InMemoryEventStore eventStore,
        string agentId,
        string requestId,
        string runId,
        string definitionActorId,
        string scriptRevision,
        long queuedAtUnixTimeMs)
    {
        await eventStore.AppendAsync(
            agentId,
            [
                new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = 1,
                    EventType = ScriptDefinitionQueryQueuedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new ScriptDefinitionQueryQueuedEvent
                    {
                        RequestId = requestId,
                        RunEvent = new RunScriptRequestedEvent
                        {
                            RunId = runId,
                            InputPayload = Any.Pack(new Struct()),
                            ScriptRevision = scriptRevision,
                            DefinitionActorId = definitionActorId,
                            RequestedEventType = "chat.requested",
                        },
                        QueuedAtUnixTimeMs = queuedAtUnixTimeMs,
                        TimeoutCallbackId = RuntimeCallbackKeyComposer.BuildCallbackId(
                            "script-definition-query-timeout",
                            requestId),
                    }),
                    AgentId = agentId,
                },
            ],
            expectedVersion: 0,
            CancellationToken.None);
    }

    private sealed class RecordingOrchestrator : IScriptRuntimeExecutionOrchestrator
    {
        public List<ScriptRuntimeExecutionRequest> Requests { get; } = [];

        public Task<IReadOnlyList<IMessage>> ExecuteRunAsync(
            ScriptRuntimeExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<IMessage>>(
            [
                new ScriptRunDomainEventCommitted
                {
                    RunId = request.RunEvent.RunId ?? string.Empty,
                    ScriptRevision = request.ScriptRevision ?? string.Empty,
                    DefinitionActorId = request.RunEvent.DefinitionActorId ?? string.Empty,
                    EventType = "script.executed",
                    Payload = Any.Pack(new StringValue { Value = "ok" }),
                    ReadModelSchemaVersion = request.ReadModelSchemaVersion ?? string.Empty,
                    ReadModelSchemaHash = request.ReadModelSchemaHash ?? string.Empty,
                },
            ]);
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<PublishedMessage> Sent { get; } = [];
        public Exception? SendToException { get; set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = direction;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
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
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            if (SendToException != null)
                throw SendToException;
            Sent.Add(new PublishedMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCallbackScheduler :
        IActorRuntimeCallbackScheduler
    {
        private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);

        public List<ScheduledTimeout> Timeouts { get; } = [];
        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var generation = _generations.GetValueOrDefault(request.CallbackId, 0) + 1;
            _generations[request.CallbackId] = generation;

            var lease = new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                generation,
                RuntimeCallbackBackend.InMemory);
            Timeouts.Add(new ScheduledTimeout(
                lease,
                new RuntimeCallbackTimeoutRequest
                {
                    ActorId = request.ActorId,
                    CallbackId = request.CallbackId,
                    DueTime = request.DueTime,
                    TriggerEnvelope = request.TriggerEnvelope.Clone(),
                }));
            return Task.FromResult(lease);
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed record ScheduledTimeout(
        RuntimeCallbackLease Lease,
        RuntimeCallbackTimeoutRequest Request);

    private sealed record PublishedMessage(string TargetActorId, IMessage Payload);
}
