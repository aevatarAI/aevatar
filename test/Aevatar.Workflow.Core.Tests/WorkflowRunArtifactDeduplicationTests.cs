using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Composition;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowRunArtifactDeduplicationTests
{
    [Fact]
    public async Task ArtifactObservation_ShouldPersistOutOfOrderVersionsAndSuppressDurableDuplicates()
    {
        const string actorId = "workflow-run-artifact-dedup";
        const string publisherActorId = "workflow-run-artifact-dedup:role-a";
        var store = new InMemoryEventStore();
        var agent = CreateAgent(actorId, store);

        await agent.HandleWorkflowArtifactObservationEnvelope(
            OperationEnvelope(publisherActorId, "child-event-10", stateVersion: 10, progressSequence: 10));
        await agent.HandleWorkflowArtifactObservationEnvelope(
            OperationEnvelope(publisherActorId, "child-event-9", stateVersion: 9, progressSequence: 9));
        await agent.HandleWorkflowArtifactObservationEnvelope(
            OperationEnvelope(publisherActorId, "child-event-10", stateVersion: 10, progressSequence: 10));

        var recovered = CreateAgent(actorId, store);
        await recovered.ActivateAsync();
        await recovered.HandleWorkflowArtifactObservationEnvelope(
            OperationEnvelope(publisherActorId, "child-event-9", stateVersion: 9, progressSequence: 9));
        await recovered.HandleWorkflowArtifactObservationEnvelope(
            OperationEnvelope(publisherActorId, "child-event-8", stateVersion: 8, progressSequence: 8));

        var persistedFacts = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(WorkflowRuntimeOperationRecordedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<WorkflowRuntimeOperationRecordedEvent>())
            .ToList();

        persistedFacts.Select(fact => fact.Source.CommittedStateVersion)
            .Should().Equal(10, 9, 8);
        recovered.State.ProcessedArtifactSources
            .Select(source => source.CommittedStateVersion)
            .Should().Equal(10, 9, 8);
        recovered.State.ProcessedArtifactStateVersionsByPublisher.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, long>(publisherActorId, 10));
    }

    [Fact]
    public async Task ArtifactObservation_ShouldRejectSourceIdentityThatCannotBeDurablyDeduplicated()
    {
        const string actorId = "workflow-run-artifact-invalid-source";
        var store = new InMemoryEventStore();
        var agent = CreateAgent(actorId, store);
        var malformed = OperationEnvelope(
            publisherActorId: string.Empty,
            childEventId: string.Empty,
            stateVersion: 0,
            progressSequence: 1);

        await agent.HandleWorkflowArtifactObservationEnvelope(malformed);
        await agent.HandleWorkflowArtifactObservationEnvelope(malformed);

        (await store.GetEventsAsync(actorId))
            .Should().NotContain(stateEvent =>
                stateEvent.EventData.Is(WorkflowRuntimeOperationRecordedEvent.Descriptor));
        agent.State.ProcessedArtifactSources.Should().BeEmpty();
    }

    private static WorkflowRunGAgent CreateAgent(string actorId, InMemoryEventStore store)
    {
        var runtime = new UnsupportedActorRuntime();
        var agent = new WorkflowRunGAgent(runtime, runtime, new EmptyModuleFactory(), [])
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowRunState>(store),
            EventPublisher = new NoopEventPublisher(),
            Services = EmptyServiceProvider.Instance,
            Logger = NullLogger.Instance,
        };
        var setId = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic);
        setId.Should().NotBeNull();
        setId!.Invoke(agent, [actorId]);
        return agent;
    }

    private static EventEnvelope OperationEnvelope(
        string publisherActorId,
        string childEventId,
        long stateVersion,
        long progressSequence) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(publisherActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = childEventId,
                    Version = stateVersion,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    EventData = Any.Pack(new RoleChatSessionProgressedEvent
                    {
                        SessionId = "session-1",
                        Sequence = progressSequence,
                        ModelStarted = new RoleChatModelStartedProgress
                        {
                            OperationId = $"model-{stateVersion}",
                            Round = 0,
                            Model = "model-a",
                            Provider = "provider-a",
                        },
                    }),
                },
            }),
        };

    private sealed class EmptyModuleFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            module = null;
            return false;
        }
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IEnumerable<ICommittedStatePublicationHook>))
                return Array.Empty<ICommittedStatePublicationHook>();
            return null;
        }
    }

    private sealed class UnsupportedActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }
}
