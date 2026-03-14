using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptRuntimeGAgentEventDrivenQueryTests
{
    [Fact]
    public async Task DirectInternalSignal_ShouldDispatchThroughBehavior_WhenTypeIsDeclared()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var artifactResolver = new StaticArtifactResolver();
        var codec = new ProtobufMessageCodec();
        var agent = new ScriptBehaviorGAgent(new InternalSignalDispatcher(), new NoOpCapabilityFactory(), artifactResolver, codec)
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptBehaviorState>(eventStore),
        };

        await agent.HandleEnvelopeAsync(BuildEnvelope(new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = "source",
            SourceHash = "hash-1",
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource("source"),
            StateTypeUrl = Any.Pack(new SimpleTextState()).TypeUrl,
            ReadModelTypeUrl = Any.Pack(new SimpleTextReadModel()).TypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        }));
        await agent.HandleEnvelopeAsync(BuildEnvelope(new SimpleTextSignal { Value = "signal" }));

        agent.State.LastAppliedEventVersion.Should().Be(2);
        agent.State.LastRunId.Should().BeEmpty();
        agent.State.LastEventId.Should().Be(Any.Pack(new SimpleTextEvent()).TypeUrl);

        var persisted = await eventStore.GetEventsAsync(agent.Id, ct: CancellationToken.None);
        persisted.Should().Contain(x => x.EventData.Is(ScriptDomainFactCommitted.Descriptor));
    }

    private sealed class StaticArtifactResolver : IScriptBehaviorArtifactResolver
    {
        public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
        {
            _ = request;
            var behavior = new StatefulBehavior();
            return new ScriptBehaviorArtifact(
                "script-1",
                "rev-1",
                "hash-1",
                behavior.Descriptor,
                behavior.Descriptor.ToContract(),
                () => new StatefulBehavior());
        }
    }

    private sealed class StatefulBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnSignal<SimpleTextSignal>(HandleSignalAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty },
                    reduce: static (_, evt, _) => evt.Current)
                .OnQuery<SimpleTextQueryRequested, SimpleTextQueryResponded>(HandleQueryAsync);
        }

        private static Task HandleSignalAsync(
            SimpleTextSignal inbound,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            _ = inbound;
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextEvent
            {
                CommandId = context.CommandId,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = "signal",
                },
            });
            return Task.CompletedTask;
        }

        private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
            SimpleTextQueryRequested queryPayload,
            ScriptQueryContext<SimpleTextReadModel> snapshot,
            CancellationToken ct)
        {
            _ = queryPayload;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<SimpleTextQueryResponded?>(new SimpleTextQueryResponded
            {
                RequestId = queryPayload.RequestId ?? string.Empty,
                Current = snapshot.CurrentReadModel ?? new SimpleTextReadModel(),
            });
        }
    }

    private static EventEnvelope BuildEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("runtime-test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-1",
            },
        };

    private sealed class InternalSignalDispatcher : IScriptBehaviorDispatcher
    {
        public Task<IReadOnlyList<ScriptDomainFactCommitted>> DispatchAsync(
            ScriptBehaviorDispatchRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            request.Envelope.Payload.Should().NotBeNull();
            request.Envelope.Payload!.Is(SimpleTextSignal.Descriptor).Should().BeTrue();
            return Task.FromResult<IReadOnlyList<ScriptDomainFactCommitted>>(
            [
                new ScriptDomainFactCommitted
                {
                    ActorId = request.ActorId,
                    DefinitionActorId = request.DefinitionActorId,
                    ScriptId = request.ScriptId,
                    Revision = request.Revision,
                    RunId = string.Empty,
                    CommandId = request.Envelope.Id ?? string.Empty,
                    CorrelationId = request.Envelope.Propagation?.CorrelationId ?? string.Empty,
                    EventSequence = 1,
                    EventType = Any.Pack(new SimpleTextEvent()).TypeUrl,
                    DomainEventPayload = Any.Pack(new SimpleTextEvent
                    {
                        CommandId = request.Envelope.Id ?? string.Empty,
                        Current = new SimpleTextReadModel
                        {
                            HasValue = true,
                            Value = "signal",
                        },
                    }),
                    StateTypeUrl = Any.Pack(new SimpleTextState()).TypeUrl,
                    ReadModelTypeUrl = Any.Pack(new SimpleTextReadModel()).TypeUrl,
                    StateVersion = request.CurrentStateVersion + 1,
                    OccurredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            ]);
        }
    }

    private sealed class NoOpCapabilityFactory : IScriptBehaviorRuntimeCapabilityFactory
    {
        public IScriptBehaviorRuntimeCapabilities Create(
            ScriptBehaviorRuntimeCapabilityContext context,
            Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
            Func<string, IMessage, CancellationToken, Task> sendToAsync,
            Func<IMessage, CancellationToken, Task> publishToSelfAsync,
            Func<string, TimeSpan, IMessage, CancellationToken, Task<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease>> scheduleSelfSignalAsync,
            Func<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync)
        {
            _ = context;
            _ = publishAsync;
            _ = sendToAsync;
            _ = publishToSelfAsync;
            _ = scheduleSelfSignalAsync;
            _ = cancelCallbackAsync;
            return new NoOpCapabilities();
        }
    }

    private sealed class NoOpCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease("runtime-1", callbackId, 0, Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));
        public Task CancelDurableCallbackAsync(Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? string.Empty);
        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;
        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task<Aevatar.Scripting.Abstractions.Queries.ScriptReadModelSnapshot?> GetReadModelSnapshotAsync(string actorId, CancellationToken ct) =>
            Task.FromResult<Aevatar.Scripting.Abstractions.Queries.ScriptReadModelSnapshot?>(null);
        public Task<Any?> ExecuteReadModelQueryAsync(string actorId, Any queryPayload, CancellationToken ct) => Task.FromResult<Any?>(null);
        public Task<Aevatar.Scripting.Abstractions.Definitions.ScriptPromotionDecision> ProposeScriptEvolutionAsync(Aevatar.Scripting.Abstractions.Definitions.ScriptEvolutionProposal proposal, CancellationToken ct) =>
            Task.FromResult(new Aevatar.Scripting.Abstractions.Definitions.ScriptPromotionDecision(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, new Aevatar.Scripting.Abstractions.Definitions.ScriptEvolutionValidationReport(false, [])));
        public Task<string> UpsertScriptDefinitionAsync(string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, CancellationToken ct) =>
            Task.FromResult(definitionActorId ?? string.Empty);
        public Task<string> SpawnScriptRuntimeAsync(string definitionActorId, string scriptRevision, string? runtimeActorId, CancellationToken ct) =>
            Task.FromResult(runtimeActorId ?? string.Empty);
        public Task RunScriptInstanceAsync(string runtimeActorId, string runId, Any? inputPayload, string scriptRevision, string definitionActorId, string requestedEventType, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PromoteRevisionAsync(string catalogActorId, string scriptId, string revision, string definitionActorId, string sourceHash, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task RollbackRevisionAsync(string catalogActorId, string scriptId, string targetRevision, string reason, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = audience;
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
            _ = targetActorId;
            _ = evt;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
