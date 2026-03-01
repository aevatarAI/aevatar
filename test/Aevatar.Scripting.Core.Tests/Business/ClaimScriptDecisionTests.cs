using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Business;

public class ClaimScriptDecisionTests
{
    [Fact]
    public async Task Should_require_manual_review_when_high_risk()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());

        var compileResult = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest("claim_orchestrator", "rev-claim-1", ClaimOrchestratorSource),
            CancellationToken.None);

        var decision = await compileResult.CompiledDefinition!.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope("claim.submitted", "{\"caseId\":\"Case-B\",\"riskScore\":0.91,\"compliancePassed\":true}"),
            new ScriptExecutionContext(
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
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());

        var compileResult = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest("claim_orchestrator", "rev-claim-1", ClaimOrchestratorSource),
            CancellationToken.None);

        var decision = await compileResult.CompiledDefinition!.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope("claim.submitted", "{\"caseId\":\"Case-A\",\"riskScore\":0.12,\"compliancePassed\":true}"),
            new ScriptExecutionContext(
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
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());

        var compileResult = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest("claim_orchestrator", "rev-claim-1", ClaimOrchestratorSource),
            CancellationToken.None);

        var decision = await compileResult.CompiledDefinition!.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope("claim.submitted", "{\"caseId\":\"Case-C\",\"riskScore\":0.35,\"compliancePassed\":false}"),
            new ScriptExecutionContext(
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ClaimOrchestratorScript : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var input = JsonSerializer.Deserialize<ClaimCaseInput>(
            requestedEvent.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new ClaimCaseInput();

        var events = new List<IMessage>
        {
            new StringValue { Value = "ClaimFactsExtractionRequestedEvent" },
            new StringValue { Value = "ClaimRiskScoringRequestedEvent" },
            new StringValue { Value = "ClaimComplianceValidationRequestedEvent" }
        };

        if (string.Equals(input.CaseId, "Case-A", StringComparison.Ordinal))
        {
            events.Add(new StringValue { Value = "ClaimApprovedEvent" });
            return Task.FromResult(new ScriptHandlerResult(events));
        }

        if (string.Equals(input.CaseId, "Case-B", StringComparison.Ordinal) || input.RiskScore >= 0.85m)
        {
            events.Add(new StringValue { Value = "ClaimManualReviewRequestedEvent" });
            return Task.FromResult(new ScriptHandlerResult(events));
        }

        if (string.Equals(input.CaseId, "Case-C", StringComparison.Ordinal) || !input.CompliancePassed)
        {
            events.Add(new StringValue { Value = "ClaimRejectedEvent" });
            return Task.FromResult(new ScriptHandlerResult(events));
        }

        events.Add(new StringValue { Value = "ClaimApprovedEvent" });
        return Task.FromResult(new ScriptHandlerResult(events));
    }

    public ValueTask<string> ApplyDomainEventAsync(string currentStateJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult("{\"last_event\":\"" + domainEvent.EventType + "\"}");
    }

    public ValueTask<string> ReduceReadModelAsync(string currentReadModelJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult("{\"decision\":\"" + domainEvent.EventType + "\"}");
    }

    private sealed class ClaimCaseInput
    {
        public string CaseId { get; set; } = string.Empty;
        public decimal RiskScore { get; set; }
        public bool CompliancePassed { get; set; }
    }
}
""";
}
