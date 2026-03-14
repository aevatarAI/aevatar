using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptBehaviorDispatcherTests
{
    private static readonly string SimpleTextStateTypeUrl = Any.Pack(new SimpleTextState()).TypeUrl;
    private static readonly string SimpleTextReadModelTypeUrl = Any.Pack(new SimpleTextReadModel()).TypeUrl;
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
                        InputPayload = Any.Pack(new SimpleTextCommand
                        {
                            CommandId = "command-1",
                            Value = "  hello ",
                        }),
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
        fact.StateTypeUrl.Should().Be(SimpleTextStateTypeUrl);
        fact.ReadModelTypeUrl.Should().Be(SimpleTextReadModelTypeUrl);
        fact.DomainEventPayload.Should().NotBeNull();
        fact.DomainEventPayload.Unpack<SimpleTextEvent>().Current.Value.Should().Be("HELLO");
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
                        InputPayload = Any.Pack(new SimpleTextSignal
                        {
                            Value = "not-a-command",
                        }),
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rejected command payload type*aevatar.scripting.tests.SimpleTextSignal*");
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
                        InputPayload = Any.Pack(new SimpleTextCommand
                        {
                            CommandId = "command-3",
                            Value = "ok",
                        }),
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*undeclared domain event type*" + "type.googleapis.com/aevatar.scripting.tests.SimpleTextUnexpectedEvent*");
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

    private sealed class UppercaseBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>(HandleAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty },
                    reduce: static (_, evt, _) => evt.Current)
                .OnQuery<SimpleTextQueryRequested, SimpleTextQueryResponded>(HandleQueryAsync);
        }

        private static Task HandleAsync(
            SimpleTextCommand inbound,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextEvent
            {
                CommandId = inbound.CommandId ?? string.Empty,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = (inbound.Value ?? string.Empty).Trim().ToUpperInvariant(),
                },
            });
            return Task.CompletedTask;
        }

        private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
            SimpleTextQueryRequested query,
            ScriptQueryContext<SimpleTextReadModel> snapshot,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<SimpleTextQueryResponded?>(new SimpleTextQueryResponded
            {
                RequestId = query.RequestId ?? string.Empty,
                Current = snapshot.CurrentReadModel ?? new SimpleTextReadModel(),
            });
        }
    }

    private sealed class InvalidEventBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>(HandleAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty },
                    reduce: static (_, evt, _) => evt.Current)
                .OnQuery<SimpleTextQueryRequested, SimpleTextQueryResponded>(HandleQueryAsync);
        }

        private static Task HandleAsync(
            SimpleTextCommand inbound,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            _ = inbound;
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextUnexpectedEvent { Value = "unexpected" });
            return Task.CompletedTask;
        }

        private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
            SimpleTextQueryRequested query,
            ScriptQueryContext<SimpleTextReadModel> snapshot,
            CancellationToken ct)
        {
            _ = query;
            _ = snapshot;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<SimpleTextQueryResponded?>(null);
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
