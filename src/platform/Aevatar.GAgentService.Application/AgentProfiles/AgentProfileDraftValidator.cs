using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class AgentProfileDraftValidator
{
    private readonly AgentProfileSkillSealer _sealer;

    public AgentProfileDraftValidator(AgentProfileSkillSealer sealer)
    {
        _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
    }

    public async Task<AgentProfileValidationReport> ValidateAsync(
        AgentProfileIdentity identity,
        AgentProfileContent draft,
        long draftRevision,
        ByteString draftSha256,
        string? nyxIdAccessToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draftSha256);

        var result = await _sealer.ResolveAndSealAsync(
            identity,
            draft,
            nyxIdAccessToken,
            ct);
        var diagnostics = result.Diagnostics
            .Take(AgentProfileValidationLimits.DiagnosticMaxCount)
            .Select(AgentProfilePolicies.NormalizeDiagnostic)
            .ToArray();
        var resolvedSkills = result.Snapshot?.SkillBindings
            .OrderBy(static binding => binding.BindingId, StringComparer.Ordinal)
            .Select(static binding => new AgentProfileSkillResolutionSummary(
                binding.BindingId,
                binding.Skill.ExactReference,
                binding.Skill.ContentSha256))
            .ToArray() ?? [];

        return new AgentProfileValidationReport(
            result.IsSuccess,
            draftRevision,
            draftSha256,
            diagnostics,
            resolvedSkills);
    }
}
