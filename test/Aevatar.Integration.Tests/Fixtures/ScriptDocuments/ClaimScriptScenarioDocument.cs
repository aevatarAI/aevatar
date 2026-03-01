using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Integration.Tests.Fixtures.ScriptDocuments;

public sealed record ClaimScriptDocumentEntry(
    string ScriptId,
    string Revision,
    string Source,
    string SourceHash);

public sealed class ClaimScriptScenarioDocument
{
    private ClaimScriptScenarioDocument(
        string documentPath,
        IReadOnlyList<ClaimScriptDocumentEntry> scripts)
    {
        DocumentPath = documentPath;
        Scripts = scripts;
    }

    public string DocumentPath { get; }
    public IReadOnlyList<ClaimScriptDocumentEntry> Scripts { get; }

    public static ClaimScriptScenarioDocument CreateEmbedded()
    {
        var entries = new List<ClaimScriptDocumentEntry>
        {
            CreateEntry("claim_orchestrator", "rev-20260301-a", ClaimOrchestratorSource),
            CreateEntry("role_claim_analyst", "rev-20260301-a", RoleClaimAnalystSource),
            CreateEntry("fraud_risk", "rev-20260301-a", FraudRiskSource),
            CreateEntry("compliance_rule", "rev-20260301-a", ComplianceRuleSource),
            CreateEntry("human_review", "rev-20260301-a", HumanReviewSource),
        };

        return new ClaimScriptScenarioDocument("embedded://claim-anti-fraud", entries);
    }

    private static string ComputeSha256Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ClaimScriptDocumentEntry CreateEntry(string scriptId, string revision, string source) =>
        new(scriptId, revision, source, ComputeSha256Hex(source));

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
        _ = context;
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

        // Case-A: low risk + compliant => approved
        if (string.Equals(input.CaseId, "Case-A", StringComparison.Ordinal))
        {
            events.Add(new StringValue { Value = "ClaimApprovedEvent" });
            return Task.FromResult(new ScriptHandlerResult(events));
        }

        // Case-B: high risk => manual review
        if (string.Equals(input.CaseId, "Case-B", StringComparison.Ordinal) || input.RiskScore >= 0.85m)
        {
            events.Add(new StringValue { Value = "ClaimManualReviewRequestedEvent" });
            return Task.FromResult(new ScriptHandlerResult(events));
        }

        // Case-C: compliance fail => rejected
        if (string.Equals(input.CaseId, "Case-C", StringComparison.Ordinal) || !input.CompliancePassed)
        {
            events.Add(new StringValue { Value = "ClaimRejectedEvent" });
            return Task.FromResult(new ScriptHandlerResult(events));
        }

        events.Add(new StringValue { Value = "ClaimApprovedEvent" });
        return Task.FromResult(new ScriptHandlerResult(events));
    }

    public ValueTask<string> ApplyDomainEventAsync(
        string currentStateJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult("{\"last_event\":\"" + domainEvent.EventType + "\"}");
    }

    public ValueTask<string> ReduceReadModelAsync(
        string currentReadModelJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
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

    private const string RoleClaimAnalystSource = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ClaimAnalystRoleScript : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        _ = requestedEvent;
        _ = context;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = "ClaimFactsExtractedEvent" } }));
    }

    public ValueTask<string> ApplyDomainEventAsync(string currentStateJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"role\":\"analyst\",\"last_event\":\"" + domainEvent.EventType + "\"}");

    public ValueTask<string> ReduceReadModelAsync(string currentReadModelJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"decision\":\"" + domainEvent.EventType + "\"}");
}
""";

    private const string FraudRiskSource = """
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class FraudRiskScript : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();

        var riskScore = requestedEvent.PayloadJson.Contains("Case-B")
            ? 0.91m
            : requestedEvent.PayloadJson.Contains("Case-A")
                ? 0.12m
                : 0.35m;

        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = "FraudRiskScoreCalculatedEvent" } },
            "{\"risk_score\":" + riskScore.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"));
    }

    public ValueTask<string> ApplyDomainEventAsync(string currentStateJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"fraud\":\"" + domainEvent.EventType + "\"}");

    public ValueTask<string> ReduceReadModelAsync(string currentReadModelJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"decision\":\"" + domainEvent.EventType + "\"}");
}
""";

    private const string ComplianceRuleSource = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ComplianceRuleScript : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();

        var evt = requestedEvent.PayloadJson.Contains("Case-C")
            ? "ComplianceValidationFailedEvent"
            : "ComplianceValidationPassedEvent";

        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = evt } }));
    }

    public ValueTask<string> ApplyDomainEventAsync(string currentStateJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"compliance\":\"" + domainEvent.EventType + "\"}");

    public ValueTask<string> ReduceReadModelAsync(string currentReadModelJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"decision\":\"" + domainEvent.EventType + "\"}");
}
""";

    private const string HumanReviewSource = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class HumanReviewScript : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = "HumanReviewTicketRequestedEvent" } },
            "{\"ticket\":\"MANUAL-REVIEW\"}"));
    }

    public ValueTask<string> ApplyDomainEventAsync(string currentStateJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"human_review\":\"" + domainEvent.EventType + "\"}");

    public ValueTask<string> ReduceReadModelAsync(string currentReadModelJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"decision\":\"" + domainEvent.EventType + "\"}");
}
""";
}
