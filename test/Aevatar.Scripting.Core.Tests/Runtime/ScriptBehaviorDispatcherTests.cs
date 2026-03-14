using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptBehaviorDispatcherTests
{
    private static readonly string StringValueTypeUrl = Any.Pack(new StringValue()).TypeUrl;
    private static readonly string StructTypeUrl = Any.Pack(new Struct()).TypeUrl;

    [Fact]
    public async Task DispatchAsync_ShouldEmitCommittedFactsWithResolvedContract()
    {
        var behavior = new UppercaseBehavior();
        var dispatcher = new ScriptBehaviorDispatcher(
            new StaticArtifactResolver(
                CreateArtifact(behavior)),
            new ProtobufMessageCodec());
        var envelope = new EventEnvelope
        {
            Id = "command-1",
            Payload = Any.Pack(new RunScriptRequestedEvent
            {
                RunId = "run-1",
                DefinitionActorId = "definition-1",
                ScriptRevision = "rev-1",
                RequestedEventType = "integration.requested",
                InputPayload = Any.Pack(new StringValue { Value = "  hello " }),
                CommandId = "command-1",
                CorrelationId = "correlation-1",
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "correlation-1",
            },
        };

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-1",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 7,
                Envelope: envelope,
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().ContainSingle();
        var fact = facts[0];
        fact.ActorId.Should().Be("runtime-1");
        fact.DefinitionActorId.Should().Be("definition-1");
        fact.ScriptId.Should().Be("script-1");
        fact.Revision.Should().Be("rev-1");
        fact.RunId.Should().Be("run-1");
        fact.CommandId.Should().Be("command-1");
        fact.CorrelationId.Should().Be("correlation-1");
        fact.StateVersion.Should().Be(8);
        fact.StateTypeUrl.Should().Be(StringValueTypeUrl);
        fact.ReadModelTypeUrl.Should().Be(StringValueTypeUrl);
        fact.DomainEventPayload.Should().NotBeNull();
        fact.DomainEventPayload.Unpack<StringValue>().Value.Should().Be("HELLO");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectRun_WhenCommandPayloadTypeIsNotDeclared()
    {
        var behavior = new UppercaseBehavior();
        var dispatcher = new ScriptBehaviorDispatcher(
            new StaticArtifactResolver(
                CreateArtifact(behavior)),
            new ProtobufMessageCodec());

        var act = () => dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-1",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 0,
                Envelope: new EventEnvelope
                {
                    Id = "command-2",
                    Payload = Any.Pack(new RunScriptRequestedEvent
                    {
                        RunId = "run-2",
                        DefinitionActorId = "definition-1",
                        ScriptRevision = "rev-1",
                        RequestedEventType = "integration.requested",
                        InputPayload = Any.Pack(new Struct()),
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rejected command payload type*google.protobuf.Struct*");
    }

    [Fact]
    public async Task DispatchAsync_ShouldReject_WhenBehaviorEmitsUndeclaredDomainEventType()
    {
        var behavior = new InvalidEventBehavior();
        var dispatcher = new ScriptBehaviorDispatcher(
            new StaticArtifactResolver(
                CreateArtifact(behavior)),
            new ProtobufMessageCodec());

        var act = () => dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-1",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 0,
                Envelope: new EventEnvelope
                {
                    Id = "command-3",
                    Payload = Any.Pack(new RunScriptRequestedEvent
                    {
                        RunId = "run-3",
                        DefinitionActorId = "definition-1",
                        ScriptRevision = "rev-1",
                        RequestedEventType = "integration.requested",
                        InputPayload = Any.Pack(new StringValue { Value = "ok" }),
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*undeclared domain event type*google.protobuf.Struct*");
    }

    private sealed class StaticArtifactResolver : IScriptBehaviorArtifactResolver
    {
        private readonly ScriptBehaviorArtifact _artifact;

        public StaticArtifactResolver(ScriptBehaviorArtifact artifact)
        {
            _artifact = artifact;
        }

        public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
        {
            request.ScriptId.Should().Be("script-1");
            request.Revision.Should().Be("rev-1");
            return _artifact;
        }
    }

    private static ScriptBehaviorArtifact CreateArtifact(IScriptBehaviorBridge behavior) =>
        new(
            "script-1",
            "rev-1",
            "hash-1",
            behavior.Descriptor,
            behavior.Descriptor.ToContract(),
            () => behavior);

    private sealed class UppercaseBehavior : ScriptBehavior<StringValue, StringValue>
    {
        protected override void Configure(IScriptBehaviorBuilder<StringValue, StringValue> builder)
        {
            builder
                .OnCommand<StringValue>(HandleAsync)
                .OnEvent<StringValue>(
                    apply: static (_, evt, _) => new StringValue { Value = evt.Value },
                    reduce: static (_, evt, _) => new StringValue { Value = evt.Value })
                .OnQuery<Empty, StringValue>(HandleQueryAsync);
        }

        private static Task HandleAsync(
            StringValue inbound,
            ScriptCommandContext<StringValue> context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            context.Emit(new StringValue { Value = (inbound.Value ?? string.Empty).Trim().ToUpperInvariant() });
            return Task.CompletedTask;
        }

        private static Task<StringValue?> HandleQueryAsync(
            Empty query,
            ScriptQueryContext<StringValue> snapshot,
            CancellationToken ct)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<StringValue?>(snapshot.CurrentReadModel == null
                ? null
                : new StringValue { Value = snapshot.CurrentReadModel.Value });
        }
    }

    private sealed class InvalidEventBehavior : ScriptBehavior<StringValue, StringValue>
    {
        protected override void Configure(IScriptBehaviorBuilder<StringValue, StringValue> builder)
        {
            builder
                .OnCommand<StringValue>(HandleAsync)
                .OnEvent<StringValue>(apply: static (_, evt, _) => evt, reduce: static (_, evt, _) => evt)
                .OnQuery<Empty, StringValue>(HandleQueryAsync);
        }

        private static Task HandleAsync(
            StringValue inbound,
            ScriptCommandContext<StringValue> context,
            CancellationToken ct)
        {
            _ = inbound;
            ct.ThrowIfCancellationRequested();
            context.Emit(new Struct());
            return Task.CompletedTask;
        }

        private static Task<StringValue?> HandleQueryAsync(
            Empty query,
            ScriptQueryContext<StringValue> snapshot,
            CancellationToken ct)
        {
            _ = query;
            _ = snapshot;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<StringValue?>(null);
        }
    }

    private sealed class NoOpCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task<RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 0, RuntimeCallbackBackend.InMemory));
        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? string.Empty);
        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;
        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task<ScriptReadModelSnapshot?> GetReadModelSnapshotAsync(string actorId, CancellationToken ct) => Task.FromResult<ScriptReadModelSnapshot?>(null);
        public Task<Any?> ExecuteReadModelQueryAsync(string actorId, Any queryPayload, CancellationToken ct) => Task.FromResult<Any?>(null);
        public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(ScriptEvolutionProposal proposal, CancellationToken ct) =>
            Task.FromResult(new ScriptPromotionDecision(
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new ScriptEvolutionValidationReport(false, [])));
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
}
