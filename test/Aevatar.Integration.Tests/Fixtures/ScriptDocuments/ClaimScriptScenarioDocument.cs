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

        var input = ParseInput(requestedEvent.Payload);

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
            var root = payload.Unpack<Struct>();
            return new ClaimCaseInput
            {
                CaseId = root.Fields.TryGetValue("caseId", out var caseId) ? caseId.StringValue : string.Empty,
                RiskScore = root.Fields.TryGetValue("riskScore", out var riskScore) ? (decimal)riskScore.NumberValue : 0m,
                CompliancePassed = root.Fields.TryGetValue("compliancePassed", out var compliance) && compliance.BoolValue,
            };
        }

        return new ClaimCaseInput();
    }
}
""";

    private const string RoleClaimAnalystSource = """
using System.Collections.Generic;
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

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct { Fields = { ["role"] = Google.Protobuf.WellKnownTypes.Value.ForString("analyst"), ["last_event"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new Struct { Fields = { ["decision"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });
}
""";

    private const string FraudRiskSource = """
using System.Collections.Generic;
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

        var caseId = requestedEvent.Payload != null && requestedEvent.Payload.Is(Struct.Descriptor)
            ? requestedEvent.Payload.Unpack<Struct>().Fields.TryGetValue("caseId", out var caseValue) ? caseValue.StringValue : string.Empty
            : string.Empty;
        var riskScore = string.Equals(caseId, "Case-B", System.StringComparison.Ordinal)
            ? 0.91m
            : string.Equals(caseId, "Case-A", System.StringComparison.Ordinal)
                ? 0.12m
                : 0.35m;

        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = "FraudRiskScoreCalculatedEvent" } },
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["risk_score"] = Google.Protobuf.WellKnownTypes.Value.ForNumber((double)riskScore),
                    },
                }),
            }));
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["fraud"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType),
                    },
                }),
            });

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new Struct { Fields = { ["decision"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });
}
""";

    private const string ComplianceRuleSource = """
using System.Collections.Generic;
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

        var caseId = requestedEvent.Payload != null && requestedEvent.Payload.Is(Struct.Descriptor)
            ? requestedEvent.Payload.Unpack<Struct>().Fields.TryGetValue("caseId", out var caseValue) ? caseValue.StringValue : string.Empty
            : string.Empty;
        var evt = string.Equals(caseId, "Case-C", System.StringComparison.Ordinal)
            ? "ComplianceValidationFailedEvent"
            : "ComplianceValidationPassedEvent";

        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = evt } }));
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["compliance"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType),
                    },
                }),
            });

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new Struct { Fields = { ["decision"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });
}
""";

    private const string HumanReviewSource = """
using System.Collections.Generic;
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
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["ticket"] = Google.Protobuf.WellKnownTypes.Value.ForString("MANUAL-REVIEW"),
                    },
                }),
            }));
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["human_review"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType),
                    },
                }),
            });

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new Struct { Fields = { ["decision"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });
}
""";
}
