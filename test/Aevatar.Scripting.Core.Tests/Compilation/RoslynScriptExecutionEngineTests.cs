using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public sealed class RoslynScriptExecutionEngineTests
{
    [Fact]
    public async Task CompiledArtifact_ShouldInstantiateBehavior_AndThrowAfterDispose()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var result = compiler.Compile(new ScriptBehaviorCompilationRequest(
            ScriptId: "script-exec",
            Revision: "rev-1",
            Source: """
                    using System.Collections.Generic;
                    using System.Threading;
                    using System.Threading.Tasks;
                    using Aevatar.Scripting.Abstractions;
                    using Aevatar.Scripting.Abstractions.Behaviors;
                    using Aevatar.Scripting.Core.Tests.Messages;

                    public sealed class ExecutionBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
                    {
                        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
                        {
                            builder
                                .OnCommand<SimpleTextCommand>(HandleCommandAsync)
                                .OnEvent<SimpleTextEvent>(
                                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty })
                                .ProjectState(static (state, _) => state == null
                                    ? null
                                    : new SimpleTextReadModel
                                    {
                                        HasValue = !string.IsNullOrWhiteSpace(state.Value),
                                        Value = state.Value ?? string.Empty,
                                    });
                        }

                        private static Task HandleCommandAsync(
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

                        private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
                            SimpleTextQueryRequested queryPayload,
                            ScriptQueryContext<SimpleTextReadModel> snapshot,
                            CancellationToken ct)
                        {
                            ct.ThrowIfCancellationRequested();
                            return Task.FromResult<SimpleTextQueryResponded?>(new SimpleTextQueryResponded
                            {
                                RequestId = queryPayload.RequestId ?? string.Empty,
                                Current = snapshot.CurrentReadModel ?? new SimpleTextReadModel(),
                            });
                        }
                    }
                    """));

        result.IsSuccess.Should().BeTrue();
        result.Artifact.Should().NotBeNull();

        await using var artifact = result.Artifact!;
        var behavior = artifact.CreateBehavior();
        try
        {
            var dispatch = await behavior.DispatchAsync(
                new SimpleTextCommand { CommandId = "command-1", Value = "HELLO" },
                new ScriptDispatchContext(
                    ActorId: "runtime-1",
                    ScriptId: "script-exec",
                    Revision: "rev-1",
                    RunId: "run-1",
                    MessageType: "integration.requested",
                    MessageId: "msg-1",
                    CommandId: "command-1",
                    CorrelationId: "corr-1",
                    CausationId: "cause-1",
                    DefinitionActorId: "definition-1",
                    CurrentState: null,
                    RuntimeCapabilities: new NoOpCapabilities()),
                CancellationToken.None);

            dispatch.Should().ContainSingle();
            dispatch[0].Should().BeOfType<SimpleTextEvent>().Which.Current.Value.Should().Be("HELLO");
        }
        finally
        {
            if (behavior is IDisposable disposable)
                disposable.Dispose();
        }

        await artifact.DisposeAsync();

        var act = () => artifact.CreateBehavior();
        act.Should().Throw<ObjectDisposedException>();
    }

    private sealed class NoOpCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task PublishAsync(IMessage eventPayload, Aevatar.Foundation.Abstractions.TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease("runtime-1", callbackId, 0, Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));
        public Task CancelDurableCallbackAsync(Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? string.Empty);
        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;
        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task<ScriptReadModelSnapshot?> GetReadModelSnapshotAsync(string actorId, CancellationToken ct) => Task.FromResult<ScriptReadModelSnapshot?>(null);
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
}
