using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Tests.Messages;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Contract;

public sealed class ScriptDefinitionContractsTests
{
    [Fact]
    public void EmptyContract_ShouldExposeEmptyCollectionsAndDescriptorPayload()
    {
        var contract = ScriptGAgentContract.Empty;

        contract.StateTypeUrl.Should().BeEmpty();
        contract.ReadModelTypeUrl.Should().BeEmpty();
        contract.CommandTypeUrls.Should().BeEmpty();
        contract.DomainEventTypeUrls.Should().BeEmpty();
        contract.InternalSignalTypeUrls.Should().BeEmpty();
        contract.StateDescriptorFullName.Should().BeEmpty();
        contract.ReadModelDescriptorFullName.Should().BeEmpty();
        contract.ProtocolDescriptorSet.Should().NotBeNull();
        contract.ProtocolDescriptorSet!.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void DispatchContext_ShouldRetainTypedStateAndRuntimeCapabilities()
    {
        var capabilities = new TestCapabilities();
        var context = new ScriptDispatchContext(
            ActorId: "runtime-1",
            ScriptId: "script-1",
            Revision: "rev-1",
            RunId: "run-1",
            MessageType: "integration.requested",
            MessageId: "message-1",
            CommandId: "command-1",
            CorrelationId: "corr-1",
            CausationId: "cause-1",
            DefinitionActorId: "definition-1",
            CurrentState: new ScriptProfileState
            {
                CommandCount = 2,
                LastCommandId = "command-0",
                NormalizedText = "HELLO",
            },
            RuntimeCapabilities: capabilities);

        context.RuntimeCapabilities.Should().BeSameAs(capabilities);
        context.CurrentState.Should().BeOfType<ScriptProfileState>().Which.CommandCount.Should().Be(2);
    }

    private sealed class TestCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult(prompt);
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
}
