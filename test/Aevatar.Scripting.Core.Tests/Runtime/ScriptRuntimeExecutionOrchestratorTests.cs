using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptRuntimeExecutionOrchestratorTests
{
    [Fact]
    public async Task DispatchAsync_ShouldDisposeBehavior_WhenBehaviorImplementsAsyncDisposable()
    {
        var tracker = new AsyncDisposableTracker();
        var behavior = new AsyncDisposableBehavior(tracker);
        var dispatcher = new ScriptBehaviorDispatcher(
            new StaticResolver(new ScriptBehaviorArtifact(
                "script-1",
                "rev-1",
                "hash-1",
                behavior.Descriptor,
                behavior.Descriptor.ToContract(),
                () => new AsyncDisposableBehavior(tracker))),
            new ProtobufMessageCodec());

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-1",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "source",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 0,
                Envelope: new EventEnvelope
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
                            Value = "hello",
                        }),
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().ContainSingle();
        tracker.DisposeCalls.Should().Be(1);
    }

    private sealed class StaticResolver(ScriptBehaviorArtifact artifact) : IScriptBehaviorArtifactResolver
    {
        public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
        {
            _ = request;
            return artifact;
        }
    }

    private sealed class AsyncDisposableTracker
    {
        public int DisposeCalls { get; private set; }

        public void MarkDisposed() => DisposeCalls++;
    }

    private sealed class AsyncDisposableBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>, IAsyncDisposable
    {
        private readonly AsyncDisposableTracker _tracker;

        public AsyncDisposableBehavior(AsyncDisposableTracker tracker)
        {
            _tracker = tracker;
        }

        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>(HandleAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty },
                    reduce: static (_, evt, _) => evt.Current);
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
                    Value = inbound.Value ?? string.Empty,
                },
            });
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _tracker.MarkDisposed();
            return ValueTask.CompletedTask;
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
            Task.FromResult(new ScriptPromotionDecision(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, new ScriptEvolutionValidationReport(false, [])));
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
