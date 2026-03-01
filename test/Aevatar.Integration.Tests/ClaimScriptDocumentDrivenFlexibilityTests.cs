using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Integration.Tests.Fixtures.ScriptDocuments;
using FluentAssertions;
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
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());

        foreach (var script in document.Scripts)
        {
            var compilation = await compiler.CompileAsync(
                new ScriptCompilationRequest(
                    script.ScriptId,
                    script.Revision,
                    script.Source),
                CancellationToken.None);

            compilation.IsSuccess.Should().BeTrue(
                $"script `{script.ScriptId}` should compile from script document");

            var definition = new ScriptDefinitionGAgent
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
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());

        var compilation = await compiler.CompileAsync(
            new ScriptCompilationRequest(orchestrator.ScriptId, orchestrator.Revision, orchestrator.Source),
            CancellationToken.None);
        compilation.IsSuccess.Should().BeTrue();

        var decision = await compilation.CompiledDefinition!.DecideAsync(
            new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                "runtime-1",
                orchestrator.ScriptId,
                orchestrator.Revision),
            CancellationToken.None);

        var runtimeWithoutDependencies = new ScriptRuntimeGAgent
        {
            EventSourcingBehaviorFactory =
                new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(new InMemoryEventStore()),
        };
        Func<Task> missingDefinitionAct = () => runtimeWithoutDependencies.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-snapshot-check",
            InputJson = "{\"case\":\"Case-B\"}",
            ScriptRevision = orchestrator.Revision,
            DefinitionActorId = "missing-definition-actor",
        });
        await missingDefinitionAct.Should().ThrowAsync<InvalidOperationException>();

        var supportsDynamicDecisionEvents = decision.DomainEvents.Count > 0;
        var enforcesDefinitionSnapshotLookup = true;
        var hasFactoryPort = Type.GetType(
                "Aevatar.Scripting.Core.Ports.IGAgentFactoryPort, Aevatar.Scripting.Core",
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

        var upsertAdapter = new UpsertScriptDefinitionCommandAdapter();
        await definitionActor.HandleEventAsync(
            upsertAdapter.Map(
                new UpsertScriptDefinitionCommand(
                    ScriptId: orchestrator.ScriptId,
                    ScriptRevision: orchestrator.Revision,
                    SourceText: orchestrator.Source,
                    SourceHash: orchestrator.SourceHash),
                definitionActorId),
            CancellationToken.None);

        var runAdapter = new RunScriptCommandAdapter();
        await runtimeActor.HandleEventAsync(
            runAdapter.Map(
                new RunScriptCommand(
                    RunId: "run-flex",
                    InputJson: "{\"caseId\":\"Case-B\",\"riskScore\":0.91,\"compliancePassed\":true}",
                    ScriptRevision: orchestrator.Revision,
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        var runtimeState = ((ScriptRuntimeGAgent)runtimeActor.Agent).State;
        var supportsCustomRuntimeState = runtimeState.StatePayloadJson.Contains(
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
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());

        var compilation = await compiler.CompileAsync(
            new ScriptCompilationRequest(orchestrator.ScriptId, orchestrator.Revision, orchestrator.Source),
            CancellationToken.None);

        compilation.IsSuccess.Should().BeTrue();

        var decision = await compilation.CompiledDefinition!.DecideAsync(
            new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                ActorId: "runtime-claim",
                ScriptId: orchestrator.ScriptId,
                Revision: orchestrator.Revision,
                RunId: "run-case-b",
                CorrelationId: "corr-case-b",
                InputJson: "{\"caseId\":\"Case-B\",\"riskScore\":0.91,\"compliancePassed\":true}"),
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
}
