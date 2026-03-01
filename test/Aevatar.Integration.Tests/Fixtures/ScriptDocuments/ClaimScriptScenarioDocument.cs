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

        // Case-A: low risk + compliant => approved
        if (string.Equals(input.CaseId, "Case-A", StringComparison.Ordinal))
        {
            events.Add("ClaimApprovedEvent");
            return events;
        }

        // Case-B: high risk => manual review
        if (string.Equals(input.CaseId, "Case-B", StringComparison.Ordinal) || input.RiskScore >= 0.85m)
        {
            events.Add("ClaimManualReviewRequestedEvent");
            return events;
        }

        // Case-C: compliance fail => rejected
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

    private const string RoleClaimAnalystSource = """
using System;

public static class ClaimAnalystRoleScript
{
    public static string BuildPrompt(string claimNarrative, string policySummary)
    {
        return
            "Extract structured claim facts for anti-fraud and compliance review. " +
            "Narrative=" + claimNarrative + "; Policy=" + policySummary;
    }
}
""";

    private const string FraudRiskSource = """
using System;

public static class FraudRiskScript
{
    public static decimal Score(string caseId)
    {
        if (string.Equals(caseId, "Case-B", StringComparison.Ordinal))
            return 0.91m;
        if (string.Equals(caseId, "Case-A", StringComparison.Ordinal))
            return 0.12m;
        return 0.35m;
    }
}
""";

    private const string ComplianceRuleSource = """
using System;

public static class ComplianceRuleScript
{
    public static bool Validate(string caseId)
    {
        if (string.Equals(caseId, "Case-C", StringComparison.Ordinal))
            return false;
        return true;
    }
}
""";

    private const string HumanReviewSource = """
using System;

public static class HumanReviewScript
{
    public static string RequestTicket(string claimCaseId, string reason)
    {
        return "MANUAL-REVIEW:" + claimCaseId + ":" + reason;
    }
}
""";
}
