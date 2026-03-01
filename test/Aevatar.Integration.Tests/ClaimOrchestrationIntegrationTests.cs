using Aevatar.Integration.Tests.Fixtures.ScriptDocuments;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

public class ClaimOrchestrationIntegrationTests
{
    [Fact]
    public async Task Should_call_agents_via_invocation_and_factory_ports_only()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestratorScript = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var compilation = await compiler.CompileAsync(
            new ScriptCompilationRequest(orchestratorScript.ScriptId, orchestratorScript.Revision, orchestratorScript.Source),
            CancellationToken.None);

        var invocationPort = new RecordingInvocationPort();
        var factoryPort = new RecordingFactoryPort();
        var orchestrator = new ClaimRuntimeOrchestrator(compilation.CompiledDefinition!, invocationPort, factoryPort);

        await orchestrator.ExecuteAsync(
            runId: "run-claim-b",
            correlationId: "corr-claim-b",
            inputJson: "{\"caseId\":\"Case-B\",\"riskScore\":0.91,\"compliancePassed\":true}",
            CancellationToken.None);

        invocationPort.Calls.Should().Contain(x => x.PayloadValue == "ClaimFactsExtractionRequestedEvent");
        invocationPort.Calls.Should().Contain(x => x.PayloadValue == "ClaimRiskScoringRequestedEvent");
        invocationPort.Calls.Should().Contain(x => x.PayloadValue == "ClaimComplianceValidationRequestedEvent");
        invocationPort.Calls.Should().Contain(x => x.PayloadValue == "ClaimManualReviewRequestedEvent");
        factoryPort.Created.Should().HaveCount(1);
    }

    [Fact]
    public async Task Should_not_create_manual_review_agent_when_not_needed()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestratorScript = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var compilation = await compiler.CompileAsync(
            new ScriptCompilationRequest(orchestratorScript.ScriptId, orchestratorScript.Revision, orchestratorScript.Source),
            CancellationToken.None);

        var invocationPort = new RecordingInvocationPort();
        var factoryPort = new RecordingFactoryPort();
        var orchestrator = new ClaimRuntimeOrchestrator(compilation.CompiledDefinition!, invocationPort, factoryPort);

        await orchestrator.ExecuteAsync(
            runId: "run-claim-a",
            correlationId: "corr-claim-a",
            inputJson: "{\"caseId\":\"Case-A\",\"riskScore\":0.12,\"compliancePassed\":true}",
            CancellationToken.None);

        invocationPort.Calls.Should().Contain(x => x.PayloadValue == "ClaimApprovedEvent");
        factoryPort.Created.Should().BeEmpty();
    }

    private sealed class ClaimRuntimeOrchestrator(
        Aevatar.Scripting.Abstractions.Definitions.IScriptAgentDefinition definition,
        IGAgentInvocationPort invocationPort,
        IGAgentFactoryPort factoryPort)
    {
        public async Task ExecuteAsync(
            string runId,
            string correlationId,
            string inputJson,
            CancellationToken ct)
        {
            var decision = await definition.DecideAsync(
                new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                    ActorId: "orchestrator-runtime",
                    ScriptId: definition.ScriptId,
                    Revision: definition.Revision,
                    RunId: runId,
                    CorrelationId: correlationId,
                    InputJson: inputJson),
                ct);

            foreach (var evt in decision.DomainEvents.OfType<StringValue>())
            {
                var eventName = evt.Value ?? string.Empty;
                if (string.Equals(eventName, "ClaimManualReviewRequestedEvent", StringComparison.Ordinal))
                {
                    var reviewActorId = await factoryPort.CreateAsync(
                        typeof(ScriptRuntimeGAgent).AssemblyQualifiedName!,
                        "manual-review-" + runId,
                        ct);
                    await invocationPort.InvokeAsync(
                        reviewActorId,
                        new StringValue { Value = eventName },
                        correlationId,
                        ct);
                    continue;
                }

                await invocationPort.InvokeAsync(
                    targetAgentId: "agent-" + eventName,
                    eventPayload: new StringValue { Value = eventName },
                    correlationId: correlationId,
                    ct: ct);
            }
        }
    }

    private sealed class RecordingInvocationPort : IGAgentInvocationPort
    {
        public List<(string TargetActorId, string PayloadValue, string CorrelationId)> Calls { get; } = [];

        public Task InvokeAsync(
            string targetAgentId,
            IMessage eventPayload,
            string correlationId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var payloadValue = eventPayload is StringValue sv ? sv.Value : eventPayload.Descriptor.Name;
            Calls.Add((targetAgentId, payloadValue, correlationId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFactoryPort : IGAgentFactoryPort
    {
        public List<(string TypeName, string? ActorId)> Created { get; } = [];

        public Task<string> CreateAsync(
            string agentTypeAssemblyQualifiedName,
            string? actorId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Created.Add((agentTypeAssemblyQualifiedName, actorId));
            return Task.FromResult(actorId ?? "created-runtime");
        }

        public Task DestroyAsync(string actorId, CancellationToken ct) => Task.CompletedTask;

        public Task LinkAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;

        public Task UnlinkAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
    }
}
