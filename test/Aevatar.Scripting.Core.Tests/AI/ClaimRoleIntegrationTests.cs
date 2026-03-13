using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.AI;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.AI;

public class ClaimRoleIntegrationTests
{
    [Fact]
    public async Task Should_delegate_to_role_agent_capability_with_correlation()
    {
        var rolePort = new RecordingRoleAgentPort();
        var capability = new RoleAgentDelegateAICapability(rolePort);

        var output = await capability.AskAsync(
            runId: "run-claim-1",
            correlationId: "corr-claim-1",
            prompt: "extract claim facts",
            ct: CancellationToken.None);

        output.Should().Be("structured-facts");
        rolePort.RunId.Should().Be("run-claim-1");
        rolePort.CorrelationId.Should().Be("corr-claim-1");
        rolePort.Prompt.Should().Be("extract claim facts");
    }

    [Fact]
    public async Task Should_map_ai_output_to_typed_claim_decision_event()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(
            new ScriptBehaviorCompilationRequest(
                ScriptId: "claim-role-script",
                Revision: "rev-role-1",
                Source: ClaimScriptSources.RoleBehavior));

        compilation.IsSuccess.Should().BeTrue();
        compilation.Artifact.Should().NotBeNull();

        await using var artifact = compilation.Artifact!;
        var behavior = artifact.CreateBehavior();

        var emitted = await behavior.DispatchAsync(
            new ClaimSubmitted
            {
                CommandId = "command-1",
                CaseId = "Case-A",
                PolicyId = "POLICY-1",
                RiskScore = 0.12d,
                CompliancePassed = true,
            },
            new ScriptDispatchContext(
                ActorId: "claim-role-runtime",
                ScriptId: "claim-role-script",
                Revision: "rev-role-1",
                RunId: "run-role-1",
                MessageType: ClaimSubmitted.Descriptor.FullName,
                MessageId: "message-1",
                CommandId: "command-1",
                CorrelationId: "corr-role-1",
                CausationId: "cause-role-1",
                DefinitionActorId: "definition-1",
                CurrentState: null,
                RuntimeCapabilities: new FakeCapabilities("facts-ready")),
            CancellationToken.None);

        emitted.Should().ContainSingle();
        var decision = emitted[0].Should().BeOfType<ClaimDecisionRecorded>().Subject;
        decision.Current.DecisionStatus.Should().Be("FactsReady");
        decision.Current.AiSummary.Should().Be("facts-ready");
        decision.Current.TraceSteps.Should().ContainSingle("ai-facts-ready");
    }

    private sealed class RecordingRoleAgentPort : IRoleAgentPort
    {
        public string RunId { get; private set; } = string.Empty;
        public string CorrelationId { get; private set; } = string.Empty;
        public string Prompt { get; private set; } = string.Empty;

        public Task<string> RunAsync(
            string runId,
            string correlationId,
            string prompt,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RunId = runId;
            CorrelationId = correlationId;
            Prompt = prompt;
            return Task.FromResult("structured-facts");
        }
    }

    private sealed class FakeCapabilities(string aiResult) : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = prompt;
            return Task.FromResult(aiResult);
        }

        public Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage eventPayload,
            CancellationToken ct) =>
            Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 0, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? "created");
        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;
        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task<ScriptReadModelSnapshot?> GetReadModelSnapshotAsync(string actorId, CancellationToken ct) => Task.FromResult<ScriptReadModelSnapshot?>(null);
        public Task<Any?> ExecuteReadModelQueryAsync(string actorId, Any queryPayload, CancellationToken ct) => Task.FromResult<Any?>(null);

        public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct) =>
            Task.FromResult(
                new ScriptPromotionDecision(
                    Accepted: true,
                    ProposalId: proposal.ProposalId,
                    ScriptId: proposal.ScriptId,
                    BaseRevision: proposal.BaseRevision,
                    CandidateRevision: proposal.CandidateRevision,
                    Status: "promoted",
                    FailureReason: string.Empty,
                    DefinitionActorId: $"script-definition:{proposal.ScriptId}",
                    CatalogActorId: "script-catalog",
                    ValidationReport: new ScriptEvolutionValidationReport(true, [])));

        public Task<string> UpsertScriptDefinitionAsync(string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, CancellationToken ct) =>
            Task.FromResult(definitionActorId ?? "definition-1");

        public Task<string> SpawnScriptRuntimeAsync(string definitionActorId, string scriptRevision, string? runtimeActorId, CancellationToken ct) =>
            Task.FromResult(runtimeActorId ?? "runtime-1");

        public Task RunScriptInstanceAsync(string runtimeActorId, string runId, Any? inputPayload, string scriptRevision, string definitionActorId, string requestedEventType, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PromoteRevisionAsync(string catalogActorId, string scriptId, string revision, string definitionActorId, string sourceHash, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task RollbackRevisionAsync(string catalogActorId, string scriptId, string targetRevision, string reason, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
