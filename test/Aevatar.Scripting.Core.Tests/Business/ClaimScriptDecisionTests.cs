using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Business;

public class ClaimScriptDecisionTests
{
    [Fact]
    public async Task Should_emit_facts_risk_and_compliance_requests_in_order()
    {
        var result = await DispatchAsync(new ClaimSubmitted
        {
            CommandId = "command-case-a",
            CaseId = "Case-A",
            PolicyId = "POLICY-A",
            RiskScore = 0.12d,
            CompliancePassed = true,
        });

        result.Capabilities.SentMessages.Should().ContainInOrder(
            nameof(ClaimAnalystReviewRequested),
            nameof(ClaimFraudScoringRequested),
            nameof(ClaimComplianceCheckRequested));
        result.Decision.Current.DecisionStatus.Should().Be("Approved");
    }

    [Fact]
    public async Task Should_require_manual_review_when_high_risk()
    {
        var result = await DispatchAsync(new ClaimSubmitted
        {
            CommandId = "command-case-b",
            CaseId = "Case-B",
            PolicyId = "POLICY-B",
            RiskScore = 0.91d,
            CompliancePassed = true,
        });

        result.Decision.Current.DecisionStatus.Should().Be("ManualReview");
        result.Decision.Current.ManualReviewRequired.Should().BeTrue();
        result.Capabilities.SentMessages.Should().Contain(nameof(ClaimManualReviewRequested));
    }

    [Fact]
    public async Task Should_emit_approve_when_low_risk_and_compliant()
    {
        var result = await DispatchAsync(new ClaimSubmitted
        {
            CommandId = "command-case-a",
            CaseId = "Case-A",
            PolicyId = "POLICY-A",
            RiskScore = 0.12d,
            CompliancePassed = true,
        });

        result.Decision.Current.DecisionStatus.Should().Be("Approved");
        result.Decision.Current.ManualReviewRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Should_emit_reject_when_compliance_fails()
    {
        var result = await DispatchAsync(new ClaimSubmitted
        {
            CommandId = "command-case-c",
            CaseId = "Case-C",
            PolicyId = "POLICY-C",
            RiskScore = 0.35d,
            CompliancePassed = false,
        });

        result.Decision.Current.DecisionStatus.Should().Be("Rejected");
        result.Decision.Current.ManualReviewRequired.Should().BeFalse();
    }

    private static async Task<(ClaimDecisionRecorded Decision, RecordingCapabilities Capabilities)> DispatchAsync(
        ClaimSubmitted command)
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compileResult = compiler.Compile(
            new ScriptBehaviorCompilationRequest("claim_orchestrator", "rev-claim-1", ClaimScriptSources.DecisionBehavior));

        compileResult.IsSuccess.Should().BeTrue();
        compileResult.Artifact.Should().NotBeNull();

        await using var artifact = compileResult.Artifact!;
        var behavior = artifact.CreateBehavior();
        var capabilities = new RecordingCapabilities();
        var emitted = await behavior.DispatchAsync(
            command,
            new ScriptDispatchContext(
                ActorId: "claim-runtime-1",
                ScriptId: "claim_orchestrator",
                Revision: "rev-claim-1",
                RunId: "run-" + (command.CaseId ?? string.Empty).ToLowerInvariant(),
                MessageType: ClaimSubmitted.Descriptor.FullName,
                MessageId: "message-" + command.CommandId,
                CommandId: command.CommandId ?? string.Empty,
                CorrelationId: "corr-" + command.CommandId,
                CausationId: "cause-" + command.CommandId,
                DefinitionActorId: "definition-1",
                CurrentState: null,
                RuntimeCapabilities: capabilities),
            CancellationToken.None);

        emitted.Should().ContainSingle();
        return (emitted[0].Should().BeOfType<ClaimDecisionRecorded>().Subject, capabilities);
    }

    private sealed class RecordingCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public List<string> SentMessages { get; } = [];

        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult("normal-profile");
        public Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;

        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = targetActorId;
            SentMessages.Add(eventPayload.Descriptor.Name);
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 0, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? "created");
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
