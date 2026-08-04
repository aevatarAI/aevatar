using System.Security.Cryptography;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Application.AgentProfiles;

/// <summary>Validates resolved Ornn evidence against a draft and creates an Actor-owned publish candidate.</summary>
public sealed class AgentProfileSkillSealer : IAgentProfileSkillSealer
{
    private readonly IExactOrnnSkillResolver _resolver;
    private readonly AgentProfileValidationLimits _limits;

    public AgentProfileSkillSealer(IExactOrnnSkillResolver resolver, AgentProfileValidationLimits limits)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<AgentProfileSealingResult> ResolveAndSealAsync(
        AgentProfileIdentity identity,
        AgentProfileDraft draft,
        AgentProfileSealingContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);
        var diagnostics = new List<AgentProfileSealingDiagnostic>();
        foreach (var diagnostic in AgentProfilePolicies.ValidateDraft(draft))
            diagnostics.Add(new AgentProfileSealingDiagnostic(
                diagnostic.Code,
                diagnostic.Field,
                diagnostic.Message));

        var normalizedDraft = AgentProfileDeterminism.NormalizeDraft(draft);
        if (normalizedDraft.RuntimeProfile is null)
            return AgentProfileSealingResult.Failure(diagnostics);

        diagnostics.AddRange(_limits.Validate(normalizedDraft.RuntimeProfile));
        if (context.CurrentDraftRevision <= 0)
            diagnostics.Add(new AgentProfileSealingDiagnostic(
                "PROFILE_DRAFT_REVISION_INVALID",
                "currentDraftRevision",
                "The current draft revision must be positive."));
        if (context.NextPublishedRevision <= 0)
            diagnostics.Add(new AgentProfileSealingDiagnostic(
                "PROFILE_PUBLISHED_REVISION_INVALID",
                "nextPublishedRevision",
                "The next published revision must be positive."));
        if (context.PublishedAt == default)
            diagnostics.Add(new AgentProfileSealingDiagnostic(
                "PROFILE_PUBLISHED_AT_INVALID",
                "publishedAt",
                "The publish timestamp is required."));

        var maximumPolicy = normalizedDraft.RuntimeProfile.MaximumToolPolicy;
        diagnostics.AddRange(ValidatePolicySubset(
            normalizedDraft.RuntimeProfile.RecoveryToolPolicy,
            maximumPolicy,
            "runtimeProfile.recoveryToolPolicy"));
        foreach (var member in normalizedDraft.RuntimeProfile.Members)
        {
            diagnostics.AddRange(ValidatePolicySubset(
                member.TaskToolPolicy,
                maximumPolicy,
                $"runtimeProfile.members[{member.IntentId}].taskToolPolicy"));
        }
        if (diagnostics.Count > 0)
            return AgentProfileSealingResult.Failure(diagnostics);

        if (string.IsNullOrWhiteSpace(context.NyxIdAccessToken) && normalizedDraft.RuntimeProfile.Members.Count > 0)
        {
            return AgentProfileSealingResult.Failure(
                [new AgentProfileSealingDiagnostic(
                    "ORNN_DEPENDENCY_UNAVAILABLE",
                    "nyxIdAccessToken",
                    "NyxID authorization is required to resolve exact Ornn skills.")]);
        }

        var sealedSkills = new List<AgentProfileSealedSkillEvidence>();
        foreach (var member in normalizedDraft.RuntimeProfile.Members)
        {
            var taskTools = member.TaskToolPolicy?.ToolNames.ToHashSet(StringComparer.Ordinal) ?? [];
            var resolution = await _resolver.ResolveAsync(context.NyxIdAccessToken!, member.SkillRef, ct);
            if (!resolution.IsSuccess)
            {
                diagnostics.Add(new AgentProfileSealingDiagnostic(
                    resolution.DiagnosticCode!,
                    $"runtimeProfile.members[{member.IntentId}].skillRef",
                    "The exact Ornn skill could not be resolved."));
                continue;
            }

            var package = resolution.Package!;
            var memberDiagnosticCount = diagnostics.Count;
            if (!string.Equals(package.SkillGuid, member.SkillRef.Guid, StringComparison.Ordinal) ||
                !string.Equals(package.LiteralVersion, member.SkillRef.LiteralVersion, StringComparison.Ordinal))
            {
                diagnostics.Add(new AgentProfileSealingDiagnostic(
                    "ORNN_SKILL_IDENTITY_MISMATCH",
                    $"runtimeProfile.members[{member.IntentId}].skillRef",
                    "The resolved Ornn skill identity does not match the exact reference."));
            }
            if (!string.Equals(package.CanonicalName, member.ExpectedSkillName, StringComparison.Ordinal))
                diagnostics.Add(new AgentProfileSealingDiagnostic(
                    "ORNN_SKILL_IDENTITY_MISMATCH",
                    $"runtimeProfile.members[{member.IntentId}].expectedSkillName",
                    "The resolved Ornn skill name does not match the expected name."));
            if (!string.Equals(package.PublisherId, member.ReviewedPublisherId, StringComparison.Ordinal))
                diagnostics.Add(new AgentProfileSealingDiagnostic(
                    "ORNN_SKILL_PUBLISHER_MISMATCH",
                    $"runtimeProfile.members[{member.IntentId}].reviewedPublisherId",
                    "The resolved Ornn publisher does not match the reviewed publisher."));
            if (package.SkillSha256.Length != 32)
            {
                diagnostics.Add(new AgentProfileSealingDiagnostic(
                    "ORNN_SKILL_INTEGRITY_EVIDENCE_MISSING",
                    $"runtimeProfile.members[{member.IntentId}].sealedSkillSha256",
                    "The resolved Ornn skill must provide a 32-byte SHA-256 digest."));
            }
            else if (member.SealedSkillSha256.Length is not (0 or 32))
            {
                diagnostics.Add(new AgentProfileSealingDiagnostic(
                    "ORNN_SKILL_INTEGRITY_EVIDENCE_MISSING",
                    $"runtimeProfile.members[{member.IntentId}].sealedSkillSha256",
                    "The draft skill digest must be empty or a 32-byte SHA-256 digest."));
            }
            else if (member.SealedSkillSha256.Length == 32 &&
                     !CryptographicOperations.FixedTimeEquals(
                         member.SealedSkillSha256.Span,
                         package.SkillSha256.Span))
            {
                diagnostics.Add(new AgentProfileSealingDiagnostic(
                    "ORNN_SKILL_HASH_MISMATCH",
                    $"runtimeProfile.members[{member.IntentId}].sealedSkillSha256",
                    "The resolved Ornn skill digest does not match the reviewed digest."));
            }

            var declaredTools = package.DeclaredToolNames;
            if (declaredTools.Any(static name => string.IsNullOrWhiteSpace(name) || name != name.Trim()) ||
                declaredTools.Distinct(StringComparer.Ordinal).Count() != declaredTools.Count)
            {
                diagnostics.Add(new AgentProfileSealingDiagnostic(
                    "ORNN_SKILL_DECLARED_TOOLS_INVALID",
                    $"runtimeProfile.members[{member.IntentId}].taskToolPolicy",
                    "The resolved Ornn skill declares invalid or duplicate tool names."));
            }
            else if (!declaredTools.All(taskTools.Contains))
                diagnostics.Add(new AgentProfileSealingDiagnostic(
                    "ORNN_SKILL_DECLARED_TOOL_NOT_ALLOWED",
                    $"runtimeProfile.members[{member.IntentId}].taskToolPolicy",
                    "Every tool declared by the Ornn skill must be allowed by its task policy."));

            if (diagnostics.Count == memberDiagnosticCount)
            {
                sealedSkills.Add(new AgentProfileSealedSkillEvidence(
                    member.IntentId,
                    member.SkillRef.Guid,
                    member.SkillRef.LiteralVersion,
                    package.SkillSha256));
            }
        }

        if (diagnostics.Count > 0)
            return AgentProfileSealingResult.Failure(diagnostics);

        return AgentProfileSealingResult.Success(AgentProfileDeterminism.BuildPublishedSnapshot(
            identity,
            normalizedDraft,
            context.CurrentDraftRevision,
            context.NextPublishedRevision,
            context.PublishedAt,
            sealedSkills));
    }

    private static IReadOnlyList<AgentProfileSealingDiagnostic> ValidatePolicySubset(
        AgentProfileToolPolicy? policy,
        AgentProfileToolPolicy? maximum,
        string field)
    {
        var maximumToolNames = maximum?.ToolNames.ToHashSet(StringComparer.Ordinal) ?? [];
        var maximumToolSetRefs = maximum?.ToolSetRefs.ToHashSet(StringComparer.Ordinal) ?? [];
        var toolNames = policy?.ToolNames.ToHashSet(StringComparer.Ordinal) ?? [];
        var toolSetRefs = policy?.ToolSetRefs.ToHashSet(StringComparer.Ordinal) ?? [];
        return toolNames.IsSubsetOf(maximumToolNames) && toolSetRefs.IsSubsetOf(maximumToolSetRefs)
            ? []
            : [new AgentProfileSealingDiagnostic(
                "PROFILE_TOOL_POLICY_EXCEEDS_MAXIMUM",
                field,
                "The tool policy must be a subset of the Profile maximum policy.")];
    }
}
