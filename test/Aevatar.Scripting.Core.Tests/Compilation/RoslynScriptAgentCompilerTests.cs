using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;
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
}
