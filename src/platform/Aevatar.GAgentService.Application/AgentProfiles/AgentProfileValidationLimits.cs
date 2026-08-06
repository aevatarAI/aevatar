using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Application.AgentProfiles;

/// <summary>Stable profile limits formerly enforced only by the Host configuration validator.</summary>
public sealed class AgentProfileValidationLimits
{
    public const int RequiredMaxPlanSteps = 4;
    public const int RequiredHandoffTtlSeconds = 900;
    public const int RequiredClassifierTimeoutMs = 600;
    public const int RequiredExactSkillFetchTimeoutMs = 1_500;
    public const int RequiredMaxSelectedSkillBytes = 24_576;
    public const int MaximumMembers = 32;

    public IReadOnlyList<AgentProfileSealingDiagnostic> Validate(AgentProfileSnapshot runtimeProfile)
    {
        ArgumentNullException.ThrowIfNull(runtimeProfile);
        var diagnostics = new List<AgentProfileSealingDiagnostic>();
        if (runtimeProfile.Members.Count is < 1 or > MaximumMembers)
            diagnostics.Add(new(
                "PROFILE_MEMBER_LIMIT_INVALID",
                "runtimeProfile.members",
                $"Profile members must contain between 1 and {MaximumMembers} entries."));
        if (runtimeProfile.MaxPlanSteps != RequiredMaxPlanSteps)
            diagnostics.Add(new(
                "PROFILE_MAX_PLAN_STEPS_INVALID",
                "runtimeProfile.maxPlanSteps",
                $"maxPlanSteps must be {RequiredMaxPlanSteps}."));
        if (runtimeProfile.HandoffTtlSeconds != RequiredHandoffTtlSeconds)
            diagnostics.Add(new(
                "PROFILE_HANDOFF_TTL_INVALID",
                "runtimeProfile.handoffTtlSeconds",
                $"handoffTtlSeconds must be {RequiredHandoffTtlSeconds}."));
        if (runtimeProfile.ClassifierTimeoutMs != RequiredClassifierTimeoutMs)
            diagnostics.Add(new(
                "PROFILE_CLASSIFIER_TIMEOUT_INVALID",
                "runtimeProfile.classifierTimeoutMs",
                $"classifierTimeoutMs must be {RequiredClassifierTimeoutMs}."));
        if (runtimeProfile.ExactSkillFetchTimeoutMs != RequiredExactSkillFetchTimeoutMs)
            diagnostics.Add(new(
                "PROFILE_EXACT_SKILL_TIMEOUT_INVALID",
                "runtimeProfile.exactSkillFetchTimeoutMs",
                $"exactSkillFetchTimeoutMs must be {RequiredExactSkillFetchTimeoutMs}."));
        if (runtimeProfile.MaxSelectedSkillBytes != RequiredMaxSelectedSkillBytes)
            diagnostics.Add(new(
                "PROFILE_SELECTED_SKILL_BYTES_INVALID",
                "runtimeProfile.maxSelectedSkillBytes",
                $"maxSelectedSkillBytes must be {RequiredMaxSelectedSkillBytes}."));
        if (runtimeProfile.ActivationMode is not (AgentProfileActivationMode.Shadow or AgentProfileActivationMode.Enforced))
            diagnostics.Add(new(
                "PROFILE_ACTIVATION_MODE_INVALID",
                "runtimeProfile.activationMode",
                "activationMode must be SHADOW or ENFORCED."));
        return diagnostics;
    }
}
