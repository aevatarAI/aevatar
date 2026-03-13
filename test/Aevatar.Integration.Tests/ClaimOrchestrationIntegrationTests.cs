using Aevatar.Integration.Tests.Fixtures.ScriptDocuments;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

public class ClaimOrchestrationIntegrationTests
{
    [Fact]
    public void Should_not_resolve_agents_from_IServiceProvider()
    {
        typeof(ScriptCommandContext<ClaimCaseState>).GetProperty("Services").Should().BeNull();
        typeof(IScriptBehaviorRuntimeCapabilities).GetMethod(
            "GetRequiredService",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static).Should().BeNull();
    }

    [Fact]
    public async Task Should_call_agents_via_runtime_capabilities_only()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestratorScript = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(
            new ScriptBehaviorCompilationRequest(orchestratorScript.ScriptId, orchestratorScript.Revision, orchestratorScript.Source));
        compilation.IsSuccess.Should().BeTrue();
        compilation.Artifact.Should().NotBeNull();

        await using var artifact = compilation.Artifact!;
        var behavior = artifact.CreateBehavior();
        var capabilities = new RecordingCapabilities("high-risk-profile");

        var emitted = await behavior.DispatchAsync(
            new ClaimSubmitted
            {
                CommandId = "command-case-b",
                CaseId = "Case-B",
                PolicyId = "POLICY-B",
                RiskScore = 0.91d,
                CompliancePassed = true,
            },
            new ScriptDispatchContext(
                ActorId: "orchestrator-runtime",
                ScriptId: orchestratorScript.ScriptId,
                Revision: orchestratorScript.Revision,
                RunId: "run-claim-b",
                MessageType: ClaimSubmitted.Descriptor.FullName,
                MessageId: "message-1",
                CommandId: "command-case-b",
                CorrelationId: "corr-claim-b",
                CausationId: "cause-claim-b",
                DefinitionActorId: "definition-1",
                CurrentState: null,
                RuntimeCapabilities: capabilities),
            CancellationToken.None);

        capabilities.SendCalls.Select(static x => x.MessageType).Should().ContainInOrder(
            nameof(ClaimAnalystReviewRequested),
            nameof(ClaimFraudScoringRequested),
            nameof(ClaimComplianceCheckRequested),
            nameof(ClaimManualReviewRequested));
        capabilities.CreateCalls.Should().ContainSingle();
        capabilities.CreateCalls[0].ActorId.Should().Be("human-review-run-claim-b");
        emitted.Should().ContainSingle();
        emitted[0].Should().BeOfType<ClaimDecisionRecorded>()
            .Which.Current.DecisionStatus.Should().Be("ManualReview");
    }

    [Fact]
    public async Task Should_not_create_manual_review_agent_when_not_needed()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestratorScript = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(
            new ScriptBehaviorCompilationRequest(orchestratorScript.ScriptId, orchestratorScript.Revision, orchestratorScript.Source));
        compilation.IsSuccess.Should().BeTrue();
        compilation.Artifact.Should().NotBeNull();

        await using var artifact = compilation.Artifact!;
        var behavior = artifact.CreateBehavior();
        var capabilities = new RecordingCapabilities("normal-profile");

        var emitted = await behavior.DispatchAsync(
            new ClaimSubmitted
            {
                CommandId = "command-case-a",
                CaseId = "Case-A",
                PolicyId = "POLICY-A",
                RiskScore = 0.12d,
                CompliancePassed = true,
            },
            new ScriptDispatchContext(
                ActorId: "orchestrator-runtime",
                ScriptId: orchestratorScript.ScriptId,
                Revision: orchestratorScript.Revision,
                RunId: "run-claim-a",
                MessageType: ClaimSubmitted.Descriptor.FullName,
                MessageId: "message-2",
                CommandId: "command-case-a",
                CorrelationId: "corr-claim-a",
                CausationId: "cause-claim-a",
                DefinitionActorId: "definition-1",
                CurrentState: null,
                RuntimeCapabilities: capabilities),
            CancellationToken.None);

        capabilities.CreateCalls.Should().BeEmpty();
        capabilities.SendCalls.Select(static x => x.MessageType).Should().ContainInOrder(
            nameof(ClaimAnalystReviewRequested),
            nameof(ClaimFraudScoringRequested),
            nameof(ClaimComplianceCheckRequested));
        emitted[0].Should().BeOfType<ClaimDecisionRecorded>()
            .Which.Current.DecisionStatus.Should().Be("Approved");
    }

    private sealed class RecordingCapabilities(string aiOutput) : IScriptBehaviorRuntimeCapabilities
    {
        public List<(string TargetActorId, string MessageType)> SendCalls { get; } = [];
        public List<(string AgentType, string? ActorId)> CreateCalls { get; } = [];

        public Task<string> AskAIAsync(string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(aiOutput);
        }

        public Task PublishAsync(IMessage eventPayload, Aevatar.Foundation.Abstractions.TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;

        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SendCalls.Add((targetActorId, eventPayload.Descriptor.Name));
            return Task.CompletedTask;
        }

        public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage eventPayload,
            CancellationToken ct) =>
            Task.FromResult(new Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease("runtime-1", callbackId, 0, Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;

        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CreateCalls.Add((agentTypeAssemblyQualifiedName, actorId));
            return Task.FromResult(actorId ?? "created");
        }

        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;
        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task<ScriptReadModelSnapshot?> GetReadModelSnapshotAsync(string actorId, CancellationToken ct) => Task.FromResult<ScriptReadModelSnapshot?>(null);
        public Task<Any?> ExecuteReadModelQueryAsync(string actorId, Any queryPayload, CancellationToken ct) => Task.FromResult<Any?>(null);
        public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(ScriptEvolutionProposal proposal, CancellationToken ct) => Task.FromResult(new ScriptPromotionDecision(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, new ScriptEvolutionValidationReport(false, [])));
        public Task<string> UpsertScriptDefinitionAsync(string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, CancellationToken ct) => Task.FromResult(definitionActorId ?? string.Empty);
        public Task<string> SpawnScriptRuntimeAsync(string definitionActorId, string scriptRevision, string? runtimeActorId, CancellationToken ct) => Task.FromResult(runtimeActorId ?? string.Empty);
        public Task RunScriptInstanceAsync(string runtimeActorId, string runId, Any? inputPayload, string scriptRevision, string definitionActorId, string requestedEventType, CancellationToken ct) => Task.CompletedTask;
        public Task PromoteRevisionAsync(string catalogActorId, string scriptId, string revision, string definitionActorId, string sourceHash, string proposalId, CancellationToken ct) => Task.CompletedTask;
        public Task RollbackRevisionAsync(string catalogActorId, string scriptId, string targetRevision, string reason, string proposalId, CancellationToken ct) => Task.CompletedTask;
    }
}
