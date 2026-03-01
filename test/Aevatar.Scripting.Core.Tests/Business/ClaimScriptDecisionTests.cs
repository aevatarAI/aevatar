using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Business;

public class ClaimScriptDecisionTests
{
    [Fact]
    public async Task Should_require_manual_review_when_high_risk()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());

        var compileResult = await compiler.CompileAsync(
            new ScriptCompilationRequest("claim_orchestrator", "rev-claim-1", ClaimOrchestratorSource),
            CancellationToken.None);

        var decision = await compileResult.CompiledDefinition!.DecideAsync(
            new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                ActorId: "claim-runtime-1",
                ScriptId: "claim_orchestrator",
                Revision: "rev-claim-1",
                RunId: "run-case-b",
                CorrelationId: "corr-case-b",
                InputJson: "{\"caseId\":\"Case-B\",\"riskScore\":0.91,\"compliancePassed\":true}"),
            CancellationToken.None);

        decision.DomainEvents
            .Select(x => ((StringValue)x).Value)
            .Should()
            .Contain("ClaimManualReviewRequestedEvent");
    }

    [Fact]
    public async Task Should_emit_approve_when_low_risk_and_compliant()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());

        var compileResult = await compiler.CompileAsync(
            new ScriptCompilationRequest("claim_orchestrator", "rev-claim-1", ClaimOrchestratorSource),
            CancellationToken.None);

        var decision = await compileResult.CompiledDefinition!.DecideAsync(
            new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                ActorId: "claim-runtime-1",
                ScriptId: "claim_orchestrator",
                Revision: "rev-claim-1",
                RunId: "run-case-a",
                CorrelationId: "corr-case-a",
                InputJson: "{\"caseId\":\"Case-A\",\"riskScore\":0.12,\"compliancePassed\":true}"),
            CancellationToken.None);

        decision.DomainEvents
            .Select(x => ((StringValue)x).Value)
            .Should()
            .Contain("ClaimApprovedEvent");
    }

    [Fact]
    public async Task Should_emit_reject_when_compliance_fails()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());

        var compileResult = await compiler.CompileAsync(
            new ScriptCompilationRequest("claim_orchestrator", "rev-claim-1", ClaimOrchestratorSource),
            CancellationToken.None);

        var decision = await compileResult.CompiledDefinition!.DecideAsync(
            new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                ActorId: "claim-runtime-1",
                ScriptId: "claim_orchestrator",
                Revision: "rev-claim-1",
                RunId: "run-case-c",
                CorrelationId: "corr-case-c",
                InputJson: "{\"caseId\":\"Case-C\",\"riskScore\":0.35,\"compliancePassed\":false}"),
            CancellationToken.None);

        decision.DomainEvents
            .Select(x => ((StringValue)x).Value)
            .Should()
            .Contain("ClaimRejectedEvent");
    }

    private const string ClaimOrchestratorSource = """
using System;
using System.Collections.Generic;

public sealed record ClaimCaseInput(string CaseId, decimal RiskScore, bool CompliancePassed);

public static class ClaimOrchestratorScript
{
    public static IReadOnlyList<string> Decide(ClaimCaseInput input)
    {
        var events = new List<string>
        {
            "ClaimFactsExtractionRequestedEvent",
            "ClaimRiskScoringRequestedEvent",
            "ClaimComplianceValidationRequestedEvent"
        };

        if (string.Equals(input.CaseId, "Case-A", StringComparison.Ordinal))
        {
            events.Add("ClaimApprovedEvent");
            return events;
        }

        if (string.Equals(input.CaseId, "Case-B", StringComparison.Ordinal) || input.RiskScore >= 0.85m)
        {
            events.Add("ClaimManualReviewRequestedEvent");
            return events;
        }

        if (string.Equals(input.CaseId, "Case-C", StringComparison.Ordinal) || !input.CompliancePassed)
        {
            events.Add("ClaimRejectedEvent");
            return events;
        }

        events.Add("ClaimApprovedEvent");
        return events;
    }
}
""";
}
