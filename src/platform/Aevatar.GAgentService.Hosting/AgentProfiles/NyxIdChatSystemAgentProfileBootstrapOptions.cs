using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

public sealed class NyxIdChatSystemAgentProfileBootstrapOptions
{
    public const string SectionName = "AgentProfiles:SystemDefaultNyxIdChat";

    public bool Enabled { get; set; }

    public string ProfileSlug { get; set; } = "nyxid-chat-default";

    public string DisplayName { get; set; } = "NyxID Chat Default";

    public string Purpose { get; set; } = "Default NyxID chat profile.";

    public string Instructions { get; set; } = string.Empty;

    public string PolicyRevision { get; set; } = "v1";

    public int MaxPlanSteps { get; set; } = AgentProfileValidationLimits.RequiredMaxPlanSteps;

    public int HandoffTtlSeconds { get; set; } = AgentProfileValidationLimits.RequiredHandoffTtlSeconds;

    public int ClassifierTimeoutMs { get; set; } = AgentProfileValidationLimits.RequiredClassifierTimeoutMs;

    public int ExactSkillFetchTimeoutMs { get; set; } = AgentProfileValidationLimits.RequiredExactSkillFetchTimeoutMs;

    public int MaxSelectedSkillBytes { get; set; } = AgentProfileValidationLimits.RequiredMaxSelectedSkillBytes;

    public int MaxOwnedToolCount { get; set; } = AgentProfileValidationLimits.MaximumOwnedToolCount;

    public int MaxSchemaBytes { get; set; } = AgentProfileValidationLimits.MaximumSchemaBytes;

    public int CohortBasisPoints { get; set; } = AgentProfilePolicies.FullCohortBasisPoints;

    public TimeSpan ProjectionWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ProjectionPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public AgentProfileToolPolicyOptions MaximumToolPolicy { get; set; } = new();

    public AgentProfileToolPolicyOptions RecoveryToolPolicy { get; set; } = new();

    public List<AgentProfileSkillMemberOptions> Members { get; set; } = [];
}

public sealed class AgentProfileSkillMemberOptions
{
    public string IntentId { get; set; } = string.Empty;

    public string RoutingDescription { get; set; } = string.Empty;

    public string SkillGuid { get; set; } = string.Empty;

    public string LiteralVersion { get; set; } = string.Empty;

    public string ExpectedSkillName { get; set; } = string.Empty;

    public string ReviewedPublisherId { get; set; } = string.Empty;

    public string SideEffectClass { get; set; } = "external_handoff";

    public List<string> ExplicitTriggerAliases { get; set; } = [];

    public AgentProfileToolPolicyOptions TaskToolPolicy { get; set; } = new();
}

public sealed class AgentProfileToolPolicyOptions
{
    public List<string> ToolNames { get; set; } = [];

    public List<string> ToolSetRefs { get; set; } = [];

    public List<AgentProfileConnectedServiceSelectorOptions> ConnectedServiceSelectors { get; set; } = [];
}

public sealed class AgentProfileConnectedServiceSelectorOptions
{
    public string CatalogServiceSlug { get; set; } = string.Empty;

    public string EndpointId { get; set; } = string.Empty;

    public List<string> AllowedRisks { get; set; } = [];

    public List<string> ReadinessRequestedScopes { get; set; } = [];
}

internal static class AgentProfileRiskOptions
{
    public static AgentToolOperationRiskPayload Parse(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "read_only" or "readonly" or "read" => AgentToolOperationRiskPayload.ReadOnly,
            "write" => AgentToolOperationRiskPayload.Write,
            _ => AgentToolOperationRiskPayload.Unspecified,
        };
}
