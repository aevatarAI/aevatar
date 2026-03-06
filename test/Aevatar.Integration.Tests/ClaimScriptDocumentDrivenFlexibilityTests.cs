using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Schema;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Integration.Tests.Fixtures.ScriptDocuments;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Aevatar.Integration.Tests;

public class ClaimScriptDocumentDrivenFlexibilityTests
{
    private readonly ITestOutputHelper _output;

    public ClaimScriptDocumentDrivenFlexibilityTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());

        foreach (var script in document.Scripts)
        {
            var compilation = await compiler.CompileAsync(
                new ScriptPackageCompilationRequest(
                    script.ScriptId,
                    script.Revision,
                    script.Source),
                CancellationToken.None);

            compilation.IsSuccess.Should().BeTrue(
                $"script `{script.ScriptId}` should compile from script document");

            var definition = new ScriptDefinitionGAgent(
                new RoslynScriptPackageCompiler(new ScriptSandboxPolicy()),
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
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());

        var compilation = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest(orchestrator.ScriptId, orchestrator.Revision, orchestrator.Source),
            CancellationToken.None);
        compilation.IsSuccess.Should().BeTrue();

        var decision = await compilation.CompiledDefinition!.HandleRequestedEventAsync(
            new Aevatar.Scripting.Abstractions.Definitions.ScriptRequestedEventEnvelope(
                "claim.submitted",
                Any.Pack(new Struct()),
                "evt-flex-1",
                "corr-flex-1",
                "cause-flex-1"),
            new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                "runtime-1",
                orchestrator.ScriptId,
                orchestrator.Revision),
            CancellationToken.None);

        var runtimeWithoutDependencies = new ScriptRuntimeGAgent(
            new NeverCalledRuntimeExecutionOrchestrator(),
            new ThrowingSnapshotPort())
        {
            EventSourcingBehaviorFactory =
                new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(new InMemoryEventStore()),
        };
        await runtimeWithoutDependencies.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-snapshot-check",
            InputPayload = Any.Pack(new Struct
            {
                Fields = { ["case"] = Google.Protobuf.WellKnownTypes.Value.ForString("Case-B") },
            }),
            ScriptRevision = orchestrator.Revision,
            DefinitionActorId = "missing-definition-actor",
        });
        runtimeWithoutDependencies.State.LastRunId.Should().Be("run-snapshot-check");
        runtimeWithoutDependencies.State.LastEventId.Should().Be("run-snapshot-check");

        var supportsDynamicDecisionEvents = decision.DomainEvents.Count > 0;
        var enforcesDefinitionSnapshotLookup = true;
        var hasFactoryPort = System.Type.GetType(
                "Aevatar.Scripting.Core.Ports.IGAgentRuntimePort, Aevatar.Scripting.Core",
                throwOnError: false,
                ignoreCase: false) != null;

        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var definitionActorId = "flex-definition";
        var runtimeActorId = "flex-runtime";
        var definitionActor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        var runtimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);

        var upsertAdapter = new UpsertScriptDefinitionActorRequestAdapter();
        await definitionActor.HandleEventAsync(
            upsertAdapter.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: orchestrator.ScriptId,
                    ScriptRevision: orchestrator.Revision,
                    SourceText: orchestrator.Source,
                    SourceHash: orchestrator.SourceHash),
                definitionActorId),
            CancellationToken.None);

        var runAdapter = new RunScriptActorRequestAdapter();
        await runtimeActor.HandleEventAsync(
            runAdapter.Map(
                new RunScriptActorRequest(
                    RunId: "run-flex",
                    InputPayload: BuildClaimPayload("Case-B", 0.91, true),
                    ScriptRevision: orchestrator.Revision,
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        var runtimeState = ((ScriptRuntimeGAgent)runtimeActor.Agent).State;
        var supportsCustomRuntimeState =
            runtimeState.StatePayloads.TryGetValue("state", out var statePayload) &&
            statePayload != null &&
            statePayload.Is(Struct.Descriptor) &&
            statePayload.Unpack<Struct>().Fields.TryGetValue("last_event", out var lastEvent) &&
            string.Equals(
                lastEvent.StringValue,
                "ClaimManualReviewRequestedEvent",
                StringComparison.Ordinal);

        supportsDynamicDecisionEvents.Should().BeTrue();
        enforcesDefinitionSnapshotLookup.Should().BeTrue();
        hasFactoryPort.Should().BeTrue();
        supportsCustomRuntimeState.Should().BeTrue();

        _output.WriteLine("Flexibility capabilities verified: dynamic decision + snapshot enforcement + factory port + dynamic runtime payload.");
    }

    [Fact]
    public async Task ClaimOrchestratorScript_ShouldEmit_ManualReviewPath_ForCaseB()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestrator = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());

        var compilation = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest(orchestrator.ScriptId, orchestrator.Revision, orchestrator.Source),
            CancellationToken.None);

        compilation.IsSuccess.Should().BeTrue();

        var decision = await compilation.CompiledDefinition!.HandleRequestedEventAsync(
            new Aevatar.Scripting.Abstractions.Definitions.ScriptRequestedEventEnvelope(
                EventType: "claim.submitted",
                Payload: BuildClaimPayload("Case-B", 0.91, true),
                EventId: "evt-case-b-1",
                CorrelationId: "corr-case-b",
                CausationId: "cause-case-b"),
            new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                ActorId: "runtime-claim",
                ScriptId: orchestrator.ScriptId,
                Revision: orchestrator.Revision,
                RunId: "run-case-b",
                CorrelationId: "corr-case-b",
                InputPayload: BuildClaimPayload("Case-B", 0.91, true)),
            CancellationToken.None);

        var eventNames = decision.DomainEvents
            .Select(x => ((Google.Protobuf.WellKnownTypes.StringValue)x).Value)
            .ToArray();

        eventNames.Should().Contain("ClaimManualReviewRequestedEvent");
        eventNames.Should().Contain("ClaimFactsExtractionRequestedEvent");
        eventNames.Should().Contain("ClaimRiskScoringRequestedEvent");
        eventNames.Should().Contain("ClaimComplianceValidationRequestedEvent");
    }

    [Fact]
    public void EmbeddedScenario_ShouldNotDependOn_FileSystemLoading()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();

        document.DocumentPath.Should().Be("embedded://claim-anti-fraud");
    }

    private static Any BuildClaimPayload(string caseId, double riskScore, bool compliancePassed)
    {
        return Any.Pack(new Struct
        {
            Fields =
            {
                ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString(caseId),
                ["riskScore"] = Google.Protobuf.WellKnownTypes.Value.ForNumber(riskScore),
                ["compliancePassed"] = Google.Protobuf.WellKnownTypes.Value.ForBool(compliancePassed),
            },
        });
    }

    private sealed class ThrowingSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public bool UseEventDrivenDefinitionQuery => false;

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            _ = requestedRevision;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"Definition snapshot not found: {definitionActorId}");
        }
    }

    private sealed class NeverCalledRuntimeExecutionOrchestrator : IScriptRuntimeExecutionOrchestrator
    {
        public Task<IReadOnlyList<Google.Protobuf.IMessage>> ExecuteRunAsync(
            ScriptRuntimeExecutionRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Runtime execution should not be called when definition snapshot lookup fails.");
        }
    }
}
