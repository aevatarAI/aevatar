using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public class RoslynScriptAgentCompilerTests
{
    [Fact]
    public async Task CompileAsync_ShouldReject_WhenSandboxPolicyFails()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var request = new ScriptCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-1",
            Source: "Task.Run(() => 1);");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.CompiledDefinition.Should().BeNull();
    }

    [Fact]
    public async Task CompileAsync_ShouldReject_WhenSourceHasSyntaxError()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var request = new ScriptCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-2",
            Source: "if (true {");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.CompiledDefinition.Should().BeNull();
    }

    [Fact]
    public async Task CompileAsync_ShouldCreateDefinition_WhenSourceIsValid()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var request = new ScriptCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-3",
            Source: "var x = 1;");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
        result.CompiledDefinition.Should().NotBeNull();
        result.CompiledDefinition!.ScriptId.Should().Be("script-1");
        result.CompiledDefinition!.Revision.Should().Be("rev-3");
        result.ContractManifest.Should().NotBeNull();
        result.CompiledDefinition!.ContractManifest.Should().NotBeNull();
    }

    [Fact]
    public async Task CompileAsync_ShouldExtractContractManifest_FromSourceAnnotations()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var request = new ScriptCompilationRequest(
            ScriptId: "script-claim",
            Revision: "rev-contract",
            Source: """
// contract.input: claim_case_v1
// contract.outputs: ClaimApprovedEvent,ClaimManualReviewRequestedEvent,ClaimRejectedEvent
// contract.state: claim_runtime_state_v1
// contract.readmodel: claim_case_readmodel_v1
var x = 1;
""");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ContractManifest.Should().NotBeNull();
        result.ContractManifest!.InputSchema.Should().Be("claim_case_v1");
        result.ContractManifest.StateSchema.Should().Be("claim_runtime_state_v1");
        result.ContractManifest.ReadModelSchema.Should().Be("claim_case_readmodel_v1");
        result.ContractManifest.OutputEvents.Should().Equal(
            "ClaimApprovedEvent",
            "ClaimManualReviewRequestedEvent",
            "ClaimRejectedEvent");
        result.CompiledDefinition!.ContractManifest.Should().BeEquivalentTo(result.ContractManifest);
    }

    [Fact]
    public async Task DecideAsync_ShouldExecuteScriptBody_AndEmitEvents()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var request = new ScriptCompilationRequest(
            ScriptId: "script-claim-orchestrator",
            Revision: "rev-exec-1",
            Source: """
using System;
using System.Collections.Generic;

public sealed record ClaimInput(string CaseId, decimal RiskScore, bool CompliancePassed);

public static class ClaimScript
{
    public static IReadOnlyList<string> Decide(ClaimInput input)
    {
        var events = new List<string>
        {
            "ClaimFactsExtractionRequestedEvent",
            "ClaimRiskScoringRequestedEvent",
            "ClaimComplianceValidationRequestedEvent"
        };

        if (string.Equals(input.CaseId, "Case-B", StringComparison.Ordinal) || input.RiskScore >= 0.85m)
        {
            events.Add("ClaimManualReviewRequestedEvent");
            return events;
        }

        events.Add(input.CompliancePassed ? "ClaimApprovedEvent" : "ClaimRejectedEvent");
        return events;
    }
}
""");

        var compileResult = await compiler.CompileAsync(request, CancellationToken.None);
        compileResult.IsSuccess.Should().BeTrue();

        var decision = await compileResult.CompiledDefinition!.DecideAsync(
            new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                ActorId: "runtime-1",
                ScriptId: "script-claim-orchestrator",
                Revision: "rev-exec-1",
                InputJson: "{\"caseId\":\"Case-B\",\"riskScore\":0.91,\"compliancePassed\":true}"),
            CancellationToken.None);

        var eventNames = decision.DomainEvents
            .Select(evt => ((StringValue)evt).Value)
            .ToArray();

        eventNames.Should().ContainInOrder(
            "ClaimFactsExtractionRequestedEvent",
            "ClaimRiskScoringRequestedEvent",
            "ClaimComplianceValidationRequestedEvent",
            "ClaimManualReviewRequestedEvent");
    }

    [Fact]
    public async Task DecideAsync_ShouldAllowScriptToUseCapabilities_AndReturnStatePayload()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var request = new ScriptCompilationRequest(
            ScriptId: "script-capability-1",
            Revision: "rev-capability-1",
            Source: """
using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public static class CapabilityAwareScript
{
    public static async Task<ScriptDecisionResult> Decide(ScriptExecutionContext context, CancellationToken ct)
    {
        var aiResult = await context.Capabilities!.AskAIAsync("risk-assessment", ct);
        if (string.Equals(aiResult, "manual-review", StringComparison.Ordinal))
        {
            var reviewActorId = await context.Capabilities.CreateAgentAsync("Fake.ManualReviewAgent, Fake", "manual-" + context.RunId, ct);
            await context.Capabilities.InvokeAgentAsync(
                reviewActorId,
                new StringValue { Value = "ClaimManualReviewRequestedEvent" },
                ct);
            return new ScriptDecisionResult(
                new IMessage[] { new StringValue { Value = "ClaimManualReviewRequestedEvent" } },
                "{\"decision\":\"manual-review\"}");
        }

        return new ScriptDecisionResult(
            new IMessage[] { new StringValue { Value = "ClaimApprovedEvent" } },
            "{\"decision\":\"approved\"}");
    }
}
""");

        var compileResult = await compiler.CompileAsync(request, CancellationToken.None);
        compileResult.IsSuccess.Should().BeTrue();

        var capabilities = new RecordingScriptRuntimeCapabilities("manual-review");
        var decision = await compileResult.CompiledDefinition!.DecideAsync(
            new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                ActorId: "runtime-1",
                ScriptId: "script-capability-1",
                Revision: "rev-capability-1",
                RunId: "run-capability-1",
                CorrelationId: "corr-capability-1",
                CurrentStateJson: "{}",
                InputJson: "{\"caseId\":\"Case-B\"}",
                Capabilities: capabilities),
            CancellationToken.None);

        decision.DomainEvents.Select(x => ((StringValue)x).Value)
            .Should().ContainSingle(x => x == "ClaimManualReviewRequestedEvent");
        decision.StatePayloadJson.Should().Be("{\"decision\":\"manual-review\"}");
        capabilities.AskPrompts.Should().ContainSingle(x => x == "risk-assessment");
        capabilities.CreatedAgents.Should().ContainSingle(x => x.actorId == "manual-run-capability-1");
        capabilities.Invocations.Should().ContainSingle(x => x.target == "manual-run-capability-1" && x.eventName == "ClaimManualReviewRequestedEvent");
    }

    private sealed class RecordingScriptRuntimeCapabilities(string aiResult) : Aevatar.Scripting.Abstractions.Definitions.IScriptRuntimeCapabilities
    {
        public List<string> AskPrompts { get; } = [];
        public List<(string typeName, string? actorId)> CreatedAgents { get; } = [];
        public List<(string target, string eventName)> Invocations { get; } = [];

        public Task<string> AskAIAsync(string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AskPrompts.Add(prompt);
            return Task.FromResult(aiResult);
        }

        public Task InvokeAgentAsync(string targetAgentId, IMessage eventPayload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var eventName = eventPayload is StringValue sv ? sv.Value : eventPayload.Descriptor.Name;
            Invocations.Add((targetAgentId, eventName));
            return Task.CompletedTask;
        }

        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CreatedAgents.Add((agentTypeAssemblyQualifiedName, actorId));
            return Task.FromResult(actorId ?? "created-agent");
        }

        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;

        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;

        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
    }
}
