using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
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
        var agent = CreateAgent(orchestrator, publisher);

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
        var agent = CreateAgent(orchestrator, publisher);

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
        agent.State.PendingDefinitionQueries.Should().ContainKey(query.RequestId);

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
        agent.State.PendingDefinitionQueries.Should().NotContainKey(query.RequestId);

        await agent.HandleScriptDefinitionQueryTimeoutFired(new ScriptDefinitionQueryTimeoutFiredEvent
        {
            RequestId = query.RequestId,
            RunId = "run-1",
        });

        orchestrator.Requests.Should().ContainSingle();
        agent.State.LastRunId.Should().Be("run-1");
        agent.State.PendingDefinitionQueries.Should().NotContainKey(query.RequestId);
    }

    [Fact]
    public async Task TimeoutSignal_ShouldPersistFailure_AndIgnoreLateSnapshotResponse()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(orchestrator, publisher);

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
        agent.State.PendingDefinitionQueries.Should().ContainKey(query.RequestId);

        await agent.HandleScriptDefinitionQueryTimeoutFired(new ScriptDefinitionQueryTimeoutFiredEvent
        {
            RequestId = query.RequestId,
            RunId = "run-2",
        });

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastRunId.Should().Be("run-2");
        var versionAfterTimeout = agent.State.LastAppliedEventVersion;
        agent.State.PendingDefinitionQueries.Should().NotContainKey(query.RequestId);

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
    public async Task PendingQuery_ShouldSurviveStateReplay_AndStillExecuteOnResponse()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var eventStore = new InMemoryEventStore();
        var agent = new ScriptRuntimeGAgent(orchestrator, new EventDrivenSnapshotPort())
        {
            EventPublisher = publisher,
            Services = new ServiceCollection().BuildServiceProvider(),
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
                eventStore),
        };

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
        agent.State.PendingDefinitionQueries.Should().ContainKey(query.RequestId);

        await agent.ActivateAsync();
        agent.State.PendingDefinitionQueries.Should().ContainKey(query.RequestId);

        await agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = query.RequestId,
            Found = true,
            ScriptId = "script-replay-1",
            Revision = "rev-replay-1",
            SourceText = "public sealed class RuntimeReplayScript {}",
            ReadModelSchemaVersion = "v-replay-1",
            ReadModelSchemaHash = "hash-replay-1",
        });

        orchestrator.Requests.Should().ContainSingle();
        agent.State.LastRunId.Should().Be("run-replay-1");
        agent.State.PendingDefinitionQueries.Should().NotContainKey(query.RequestId);
    }

    [Fact]
    public async Task TimeoutSignal_WithMismatchedRunId_ShouldBeIgnored()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(orchestrator, publisher);

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
        agent.State.PendingDefinitionQueries.Should().ContainKey(query.RequestId);

        await agent.HandleScriptDefinitionQueryTimeoutFired(new ScriptDefinitionQueryTimeoutFiredEvent
        {
            RequestId = query.RequestId,
            RunId = "another-run",
        });

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastRunId.Should().BeEmpty();
        agent.State.PendingDefinitionQueries.Should().ContainKey(query.RequestId);

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
        agent.State.PendingDefinitionQueries.Should().NotContainKey(query.RequestId);
    }

    [Fact]
    public async Task DirectSnapshotFailure_ShouldPersistFailureWithoutDriftingActiveRevision()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptRuntimeGAgent(orchestrator, new FailingSnapshotPort())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
                new InMemoryEventStore()),
        };

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-fail-1",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-requested",
            DefinitionActorId = "definition-fail-1",
            RequestedEventType = "chat.requested",
        });

        orchestrator.Requests.Should().BeEmpty();
        agent.State.LastRunId.Should().Be("run-fail-1");
        agent.State.Revision.Should().BeEmpty();
        agent.State.DefinitionActorId.Should().BeEmpty();
    }

    [Fact]
    public async Task EventDrivenQuery_ShouldPersistFailure_WhenQueryDispatchFails()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher
        {
            SendToException = new InvalidOperationException("dispatch-failed"),
        };
        var agent = CreateAgent(orchestrator, publisher);

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
        agent.State.PendingDefinitionQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task SnapshotResponseNotFound_ShouldPersistFailure_AndClearPendingQuery()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(orchestrator, publisher);

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
        agent.State.PendingDefinitionQueries.Should().NotContainKey(query.RequestId);
    }

    [Fact]
    public async Task SnapshotResponseEmptySource_ShouldPersistFailure_AndClearPendingQuery()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(orchestrator, publisher);

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
        agent.State.PendingDefinitionQueries.Should().NotContainKey(query.RequestId);
    }

    [Fact]
    public async Task SnapshotResponseRevisionMismatch_ShouldPersistFailure_AndClearPendingQuery()
    {
        var orchestrator = new RecordingOrchestrator();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(orchestrator, publisher);

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
        agent.State.PendingDefinitionQueries.Should().NotContainKey(query.RequestId);
    }

    private static ScriptRuntimeGAgent CreateAgent(
        RecordingOrchestrator orchestrator,
        RecordingEventPublisher publisher)
    {
        return new ScriptRuntimeGAgent(orchestrator, new EventDrivenSnapshotPort())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
                new InMemoryEventStore()),
        };
    }

    private sealed class EventDrivenSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public bool UseEventDrivenDefinitionQuery => true;

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            _ = definitionActorId;
            _ = requestedRevision;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException("Event-driven query path should not call snapshot port directly.");
        }
    }

    private sealed class FailingSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public bool UseEventDrivenDefinitionQuery => false;

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            _ = definitionActorId;
            _ = requestedRevision;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("snapshot-load-failed");
        }
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
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = direction;
            _ = sourceEnvelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage
        {
            _ = sourceEnvelope;
            ct.ThrowIfCancellationRequested();
            if (SendToException != null)
                throw SendToException;
            Sent.Add(new PublishedMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedMessage(string TargetActorId, IMessage Payload);
}
