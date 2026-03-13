using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptRuntimeGAgentBranchCoverageTests
{
    [Fact]
    public async Task HandleEnvelopeAsync_ShouldIgnoreEnvelopeWithoutPayload()
    {
        var harness = CreateHarness();

        await harness.Agent.HandleEnvelopeAsync(new EventEnvelope
        {
            Id = "evt-empty",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("runtime-test", TopologyAudience.Self),
        });

        harness.Agent.State.LastAppliedEventVersion.Should().Be(0);
        harness.Publisher.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldTreatIdenticalBindingAsNoOp()
    {
        var harness = CreateHarness();
        var bind = new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = ScriptSources.UppercaseBehavior,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            StateTypeUrl = Any.Pack(new StringValue()).TypeUrl,
            ReadModelTypeUrl = Any.Pack(new StringValue()).TypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        };

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(bind));
        var versionAfterFirstBind = harness.Agent.State.LastAppliedEventVersion;
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(bind));

        harness.Agent.State.LastAppliedEventVersion.Should().Be(versionAfterFirstBind);
        var persisted = await harness.EventStore.GetEventsAsync(harness.Agent.Id, ct: CancellationToken.None);
        persisted.Should().ContainSingle(x => x.EventData.Is(ScriptBehaviorBoundEvent.Descriptor));
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldIgnoreIncompleteBindingQuery()
    {
        var harness = CreateHarness();
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new QueryScriptBehaviorBindingRequestedEvent
        {
            RequestId = "request-1",
            ReplyStreamId = string.Empty,
        }));

        harness.Publisher.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRejectDispatch_WhenActorIsNotBound()
    {
        var harness = CreateHarness();

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-1",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new StringValue { Value = "hello" }),
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is not bound*");
    }

    private static BranchCoverageHarness CreateHarness()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptBehaviorGAgent(
            new NoOpDispatcher(),
            new NoOpCapabilityFactory(),
            new NoOpArtifactResolver(),
            new ProtobufMessageCodec())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptBehaviorState>(eventStore),
        };

        return new BranchCoverageHarness(agent, publisher, eventStore);
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

    private sealed record BranchCoverageHarness(
        ScriptBehaviorGAgent Agent,
        RecordingEventPublisher Publisher,
        InMemoryEventStore EventStore);

    private sealed class NoOpDispatcher : IScriptBehaviorDispatcher
    {
        public Task<IReadOnlyList<ScriptDomainFactCommitted>> DispatchAsync(
            ScriptBehaviorDispatchRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptDomainFactCommitted>>([]);
        }
    }

    private sealed class NoOpArtifactResolver : IScriptBehaviorArtifactResolver
    {
        public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
        {
            throw new InvalidOperationException($"Artifact resolution should not be reached in this test. request={request}");
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
        public List<IMessage> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = audience;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Sent.Add(evt);
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
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Sent.Add(evt);
            return Task.CompletedTask;
        }
    }
}
