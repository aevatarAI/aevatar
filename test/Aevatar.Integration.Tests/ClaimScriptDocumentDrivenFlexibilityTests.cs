using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Integration.Tests.Fixtures.ScriptDocuments;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Schema;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

public class ClaimScriptDocumentDrivenFlexibilityTests
{
    [Fact]
    public void EmbeddedScenario_ShouldDefine_ComplexClaimScenario_WithCaseABAndC()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();

        document.Scripts.Select(x => x.ScriptId).Should().Contain([
            "claim_orchestrator",
            "role_claim_analyst",
            "fraud_risk",
            "compliance_rule",
            "human_review",
        ]);

        var orchestrator = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        orchestrator.Source.Should().Contain("Case-A");
        orchestrator.Source.Should().Contain("Case-B");
        orchestrator.Source.Should().Contain("Case-C");
    }

    [Fact]
    public async Task EmbeddedScripts_ShouldCompile_AndPersistIntoDefinitionState()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());

        foreach (var script in document.Scripts)
        {
            var compilation = compiler.Compile(
                new ScriptBehaviorCompilationRequest(
                    script.ScriptId,
                    script.Revision,
                    script.Source));

            compilation.IsSuccess.Should().BeTrue($"script `{script.ScriptId}` should compile from script document");

            var definition = new ScriptDefinitionGAgent(
                new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()),
                new DefaultScriptReadModelSchemaActivationPolicy())
            {
                EventSourcingBehaviorFactory =
                    new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(new InMemoryEventStore()),
            };

            await definition.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
            {
                ScriptId = script.ScriptId,
                ScriptRevision = script.Revision,
                SourceText = script.Source,
                SourceHash = script.SourceHash,
            });

            definition.State.ScriptId.Should().Be(script.ScriptId);
            definition.State.Revision.Should().Be(script.Revision);
            definition.State.SourceText.Should().Be(script.Source);
            definition.State.SourceHash.Should().Be(script.SourceHash);
        }
    }

    [Fact]
    public async Task FlexibilityAssessment_ShouldConfirm_FrameworkSupports_DeveloperCustomScripts()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestrator = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());

        var compilation = compiler.Compile(
            new ScriptBehaviorCompilationRequest(orchestrator.ScriptId, orchestrator.Revision, orchestrator.Source));
        compilation.IsSuccess.Should().BeTrue();
        compilation.Artifact.Should().NotBeNull();

        await using var artifact = compilation.Artifact!;
        artifact.Contract.CommandTypeUrls.Should().Contain(Any.Pack(new ClaimSubmitted()).TypeUrl);
        artifact.Contract.DomainEventTypeUrls.Should().Contain(Any.Pack(new ClaimDecisionRecorded()).TypeUrl);
        artifact.Contract.ReadModelDescriptorFullName.Should().Be(ClaimCaseReadModel.Descriptor.FullName);
        artifact.Contract.ProtocolDescriptorSet.Should().NotBeNull();
        artifact.Contract.ProtocolDescriptorSet!.IsEmpty.Should().BeFalse();
        typeof(IScriptBehaviorRuntimeCapabilities).GetMethod("GetRequiredService").Should().BeNull();

        await using var provider = ClaimIntegrationTestKit.BuildProvider();
        const string definitionActorId = "claim-flex-definition";
        const string runtimeActorId = "claim-flex-runtime";
        await ClaimIntegrationTestKit.UpsertOrchestratorAsync(provider, definitionActorId, CancellationToken.None);
        await ClaimIntegrationTestKit.EnsureRuntimeAsync(provider, definitionActorId, orchestrator.Revision, runtimeActorId, CancellationToken.None);

        var result = await ClaimIntegrationTestKit.RunClaimAsync(
            provider,
            definitionActorId,
            runtimeActorId,
            orchestrator.Revision,
            "run-flex",
            new ClaimSubmitted
            {
                CommandId = "run-flex",
                CaseId = "Case-B",
                PolicyId = "POLICY-B",
                RiskScore = 0.91d,
                CompliancePassed = true,
            },
            CancellationToken.None);

        result.Committed.DomainEventPayload.Should().NotBeNull();
        result.Committed.DomainEventPayload!.Unpack<ClaimDecisionRecorded>().Current.DecisionStatus.Should().Be("ManualReview");
        result.Snapshot.ReadModelPayload.Should().NotBeNull();
        result.Snapshot.ReadModelPayload!.Unpack<ClaimCaseReadModel>().DecisionStatus.Should().Be("ManualReview");
    }

    [Fact]
    public async Task ClaimOrchestratorScript_ShouldEmit_ManualReviewPath_ForCaseB()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestrator = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(
            new ScriptBehaviorCompilationRequest(orchestrator.ScriptId, orchestrator.Revision, orchestrator.Source));

        compilation.IsSuccess.Should().BeTrue();
        compilation.Artifact.Should().NotBeNull();

        await using var artifact = compilation.Artifact!;
        var behavior = artifact.CreateBehavior();
        var recorded = new RecordingCapabilities();
        var emitted = await behavior.DispatchAsync(
            new ClaimSubmitted
            {
                CommandId = "case-b",
                CaseId = "Case-B",
                PolicyId = "POLICY-B",
                RiskScore = 0.91d,
                CompliancePassed = true,
            },
            new ScriptDispatchContext(
                ActorId: "claim-runtime",
                ScriptId: orchestrator.ScriptId,
                Revision: orchestrator.Revision,
                RunId: "run-case-b",
                MessageType: ClaimSubmitted.Descriptor.FullName,
                MessageId: "message-case-b",
                CommandId: "case-b",
                CorrelationId: "corr-case-b",
                CausationId: "cause-case-b",
                DefinitionActorId: "definition-1",
                CurrentState: null,
                RuntimeCapabilities: recorded),
            CancellationToken.None);

        recorded.SendCalls.Should().Contain(nameof(ClaimManualReviewRequested));
        emitted.Should().ContainSingle();
        emitted[0].Should().BeOfType<ClaimDecisionRecorded>()
            .Which.Current.DecisionStatus.Should().Be("ManualReview");
    }

    [Fact]
    public void EmbeddedScenario_ShouldNotDependOn_FileSystemLoading()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();

        document.DocumentPath.Should().Be("embedded://claim-anti-fraud");
    }

    private sealed class RecordingCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public List<string> SendCalls { get; } = [];

        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult("high-risk-profile");
        public Task PublishAsync(IMessage eventPayload, Aevatar.Foundation.Abstractions.TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;

        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = targetActorId;
            SendCalls.Add(eventPayload.Descriptor.Name);
            return Task.CompletedTask;
        }

        public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease("runtime", callbackId, 0, Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? "created");
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
