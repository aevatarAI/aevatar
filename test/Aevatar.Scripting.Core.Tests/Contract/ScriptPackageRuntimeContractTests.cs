using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Serialization;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Contract;

public sealed class ScriptPackageRuntimeContractTests
{
    [Fact]
    public async Task CompiledBehavior_ShouldSupportDispatchApplyReduceAndQuery()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var result = compiler.Compile(new ScriptBehaviorCompilationRequest(
            ScriptId: "script-contract",
            Revision: "rev-1",
            Source: """
                    using System.Collections.Generic;
                    using System.Linq;
                    using System.Threading;
                    using System.Threading.Tasks;
                    using Aevatar.Scripting.Abstractions;
                    using Aevatar.Scripting.Abstractions.Behaviors;
                    using Aevatar.Scripting.Core.Tests.Messages;

                    public sealed class ContractBehavior : ScriptBehavior<ScriptProfileState, ScriptProfileReadModel>
                    {
                        protected override void Configure(IScriptBehaviorBuilder<ScriptProfileState, ScriptProfileReadModel> builder)
                        {
                            builder
                                .OnCommand<ScriptProfileUpdateCommand>(HandleAsync)
                                .OnEvent<ScriptProfileUpdated>(
                                    apply: static (state, evt, _) => new ScriptProfileState
                                    {
                                        CommandCount = (state?.CommandCount ?? 0) + 1,
                                        LastCommandId = evt.CommandId ?? string.Empty,
                                        NormalizedText = evt.Current?.NormalizedText ?? string.Empty,
                                    },
                                    reduce: static (_, evt, _) => evt.Current)
                                .OnQuery<ScriptProfileQueryRequested, ScriptProfileQueryResponded>(HandleQueryAsync);
                        }

                        private static Task HandleAsync(
                            ScriptProfileUpdateCommand inbound,
                            ScriptCommandContext<ScriptProfileState> context,
                            CancellationToken ct)
                        {
                            ct.ThrowIfCancellationRequested();
                            var evt = new ScriptProfileUpdated
                            {
                                CommandId = inbound.CommandId ?? string.Empty,
                                Current = new ScriptProfileReadModel
                                {
                                    HasValue = true,
                                    ActorId = inbound.ActorId ?? string.Empty,
                                    PolicyId = inbound.PolicyId ?? string.Empty,
                                    LastCommandId = inbound.CommandId ?? string.Empty,
                                    InputText = inbound.InputText ?? string.Empty,
                                    NormalizedText = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                                    Search = new ScriptProfileSearchIndex
                                    {
                                        LookupKey = $"{inbound.ActorId}:{inbound.PolicyId}".ToLowerInvariant(),
                                        SortKey = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                                    },
                                    Refs = new ScriptProfileDocumentRef
                                    {
                                        ActorId = inbound.ActorId ?? string.Empty,
                                        PolicyId = inbound.PolicyId ?? string.Empty,
                                    },
                                },
                            };
                            evt.Current.Tags.AddRange(inbound.Tags.Select(static tag => tag.Trim().ToLowerInvariant()));
                            context.Emit(evt);
                            return Task.CompletedTask;
                        }

                        private static Task<ScriptProfileQueryResponded?> HandleQueryAsync(
                            ScriptProfileQueryRequested queryPayload,
                            ScriptQueryContext<ScriptProfileReadModel> snapshot,
                            CancellationToken ct)
                        {
                            ct.ThrowIfCancellationRequested();
                            return Task.FromResult<ScriptProfileQueryResponded?>(new ScriptProfileQueryResponded
                            {
                                RequestId = queryPayload.RequestId ?? string.Empty,
                                Current = snapshot.CurrentReadModel ?? new ScriptProfileReadModel(),
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
                new ScriptProfileUpdateCommand
                {
                    CommandId = "command-1",
                    ActorId = "runtime-1",
                    PolicyId = "policy-1",
                    InputText = " hello ",
                    Tags = { "Hot", "vip" },
                },
                new ScriptDispatchContext(
                    ActorId: "runtime-1",
                    ScriptId: "script-contract",
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
            dispatch[0].Should().BeOfType<ScriptProfileUpdated>();
            dispatch[0].As<ScriptProfileUpdated>().Current.NormalizedText.Should().Be("HELLO");

            var fact = new ScriptDomainFactCommitted
            {
                ActorId = "runtime-1",
                DefinitionActorId = "definition-1",
                ScriptId = "script-contract",
                Revision = "rev-1",
                RunId = "run-1",
                EventType = Any.Pack(new ScriptProfileUpdated()).TypeUrl,
                DomainEventPayload = Any.Pack(new ScriptProfileUpdated
                {
                    CommandId = "command-1",
                    Current = new ScriptProfileReadModel
                    {
                        HasValue = true,
                        ActorId = "runtime-1",
                        PolicyId = "policy-1",
                        LastCommandId = "command-1",
                        InputText = " hello ",
                        NormalizedText = "HELLO",
                        Search = new ScriptProfileSearchIndex
                        {
                            LookupKey = "runtime-1:policy-1",
                            SortKey = "HELLO",
                        },
                        Refs = new ScriptProfileDocumentRef
                        {
                            ActorId = "runtime-1",
                            PolicyId = "policy-1",
                        },
                        Tags = { "hot", "vip" },
                    },
                }),
                StateVersion = 1,
            };

            var state = behavior.ApplyDomainEvent(
                null,
                fact.DomainEventPayload!.Unpack<ScriptProfileUpdated>(),
                new ScriptFactContext(
                    fact.ActorId,
                    fact.DefinitionActorId,
                    fact.ScriptId,
                    fact.Revision,
                    fact.RunId,
                    fact.CommandId,
                    fact.CorrelationId,
                    fact.EventSequence,
                    fact.StateVersion,
                    fact.EventType,
                    fact.OccurredAtUnixTimeMs));
            var readModel = behavior.ReduceReadModel(
                null,
                fact.DomainEventPayload!.Unpack<ScriptProfileUpdated>(),
                new ScriptFactContext(
                    fact.ActorId,
                    fact.DefinitionActorId,
                    fact.ScriptId,
                    fact.Revision,
                    fact.RunId,
                    fact.CommandId,
                    fact.CorrelationId,
                    fact.EventSequence,
                    fact.StateVersion,
                    fact.EventType,
                    fact.OccurredAtUnixTimeMs));
            var queryResult = await behavior.ExecuteQueryAsync(
                new ScriptProfileQueryRequested { RequestId = "request-1" },
                new ScriptTypedReadModelSnapshot(
                    ActorId: "runtime-1",
                    ScriptId: "script-contract",
                    DefinitionActorId: "definition-1",
                    Revision: "rev-1",
                    ReadModelTypeUrl: Any.Pack(new ScriptProfileReadModel()).TypeUrl,
                    ReadModel: readModel,
                    StateVersion: 1,
                    LastEventId: "evt-1",
                    UpdatedAt: DateTimeOffset.UtcNow),
                CancellationToken.None);

            state.Should().NotBeNull();
            state.Should().BeOfType<ScriptProfileState>().Which.CommandCount.Should().Be(1);
            readModel.Should().NotBeNull();
            readModel.Should().BeOfType<ScriptProfileReadModel>().Which.NormalizedText.Should().Be("HELLO");
            queryResult.Should().NotBeNull();
            queryResult.Should().BeOfType<ScriptProfileQueryResponded>().Which.Current.NormalizedText.Should().Be("HELLO");
        }
        finally
        {
            if (behavior is IDisposable disposable)
                disposable.Dispose();
        }
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
