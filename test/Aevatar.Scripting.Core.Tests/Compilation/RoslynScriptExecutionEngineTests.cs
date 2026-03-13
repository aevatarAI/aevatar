using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
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
                    using Google.Protobuf;
                    using Google.Protobuf.WellKnownTypes;

                    public sealed class ExecutionBehavior : ScriptBehavior<StringValue, StringValue>
                    {
                        protected override void Configure(IScriptBehaviorBuilder<StringValue, StringValue> builder)
                        {
                            builder
                                .OnCommand<StringValue>(HandleCommandAsync)
                                .OnEvent<StringValue>(
                                    apply: static (_, evt, _) => new StringValue { Value = evt.Value },
                                    reduce: static (_, evt, _) => new StringValue { Value = evt.Value })
                                .OnQuery<Empty, StringValue>(HandleQueryAsync);
                        }

                        private static Task HandleCommandAsync(
                            StringValue inbound,
                            ScriptCommandContext<StringValue> context,
                            CancellationToken ct)
                        {
                            _ = context;
                            ct.ThrowIfCancellationRequested();
                            context.Emit(new StringValue { Value = inbound.Value ?? string.Empty });
                            return Task.CompletedTask;
                        }

                        private static Task<StringValue?> HandleQueryAsync(
                            Empty queryPayload,
                            ScriptQueryContext<StringValue> snapshot,
                            CancellationToken ct)
                        {
                            _ = queryPayload;
                            ct.ThrowIfCancellationRequested();
                            return Task.FromResult<StringValue?>(snapshot.CurrentReadModel == null
                                ? null
                                : new StringValue { Value = snapshot.CurrentReadModel.Value });
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
                new StringValue { Value = "HELLO" },
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
            dispatch[0].Should().BeOfType<StringValue>().Which.Value.Should().Be("HELLO");
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
