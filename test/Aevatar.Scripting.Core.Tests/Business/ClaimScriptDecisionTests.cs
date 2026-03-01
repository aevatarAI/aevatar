using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Business;

public class ClaimScriptDecisionTests
{
    [Fact]
    public async Task Should_emit_facts_risk_and_compliance_requests_in_order()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());

        var compileResult = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest("claim_orchestrator", "rev-claim-1", ClaimOrchestratorSource),
            CancellationToken.None);

        var decision = await compileResult.CompiledDefinition!.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope("claim.submitted", BuildClaimPayload("Case-A", 0.12, true)),
            new ScriptExecutionContext(
                ActorId: "claim-runtime-1",
                ScriptId: "claim_orchestrator",
                Revision: "rev-claim-1",
                RunId: "run-order-case-a",
                CorrelationId: "corr-order-case-a",
                InputPayload: BuildClaimPayload("Case-A", 0.12, true)),
            CancellationToken.None);

        var eventNames = decision.DomainEvents
            .Select(x => ((StringValue)x).Value)
            .ToArray();
        eventNames.Should().ContainInOrder(
            "ClaimFactsExtractionRequestedEvent",
            "ClaimRiskScoringRequestedEvent",
            "ClaimComplianceValidationRequestedEvent");
    }

    [Fact]
    public async Task Should_require_manual_review_when_high_risk()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());

        var compileResult = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest("claim_orchestrator", "rev-claim-1", ClaimOrchestratorSource),
            CancellationToken.None);

        var decision = await compileResult.CompiledDefinition!.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope("claim.submitted", BuildClaimPayload("Case-B", 0.91, true)),
            new ScriptExecutionContext(
                ActorId: "claim-runtime-1",
                ScriptId: "claim_orchestrator",
                Revision: "rev-claim-1",
                RunId: "run-case-b",
                CorrelationId: "corr-case-b",
                InputPayload: BuildClaimPayload("Case-B", 0.91, true)),
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
            new ScriptRequestedEventEnvelope("claim.submitted", BuildClaimPayload("Case-A", 0.12, true)),
            new ScriptExecutionContext(
                ActorId: "claim-runtime-1",
                ScriptId: "claim_orchestrator",
                Revision: "rev-claim-1",
                RunId: "run-case-a",
                CorrelationId: "corr-case-a",
                InputPayload: BuildClaimPayload("Case-A", 0.12, true)),
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
            new ScriptRequestedEventEnvelope("claim.submitted", BuildClaimPayload("Case-C", 0.35, false)),
            new ScriptExecutionContext(
                ActorId: "claim-runtime-1",
                ScriptId: "claim_orchestrator",
                Revision: "rev-claim-1",
                RunId: "run-case-c",
                CorrelationId: "corr-case-c",
                InputPayload: BuildClaimPayload("Case-C", 0.35, false)),
            CancellationToken.None);

        decision.DomainEvents
            .Select(x => ((StringValue)x).Value)
            .Should()
            .Contain("ClaimRejectedEvent");
    }

    private const string ClaimOrchestratorSource = """
using System;
using System.Collections.Generic;
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

        var input = ParseInput(requestedEvent.Payload);

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

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct { Fields = { ["last_event"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new Struct { Fields = { ["decision"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });
    }

    private sealed class ClaimCaseInput
    {
        public string CaseId { get; set; } = string.Empty;
        public decimal RiskScore { get; set; }
        public bool CompliancePassed { get; set; }
    }

    private static ClaimCaseInput ParseInput(Any payload)
    {
        if (payload != null && payload.Is(Struct.Descriptor))
        {
            var data = payload.Unpack<Struct>();
            return new ClaimCaseInput
            {
                CaseId = data.Fields.TryGetValue("caseId", out var caseId) ? caseId.StringValue : string.Empty,
                RiskScore = data.Fields.TryGetValue("riskScore", out var risk) ? (decimal)risk.NumberValue : 0m,
                CompliancePassed = data.Fields.TryGetValue("compliancePassed", out var compliance) && compliance.BoolValue,
            };
        }

        return new ClaimCaseInput();
    }
}
""";

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
}
