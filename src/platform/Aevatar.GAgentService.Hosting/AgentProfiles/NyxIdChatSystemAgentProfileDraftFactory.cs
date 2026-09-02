using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

public static class NyxIdChatSystemAgentProfileDraftFactory
{
    public static AgentProfileDraft Create(NyxIdChatSystemAgentProfileBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var draft = new AgentProfileDraft
        {
            DisplayName = Required(options.DisplayName, nameof(options.DisplayName)),
            Purpose = Normalize(options.Purpose),
            Instructions = Required(options.Instructions, nameof(options.Instructions)),
            RuntimeProfile = new AgentProfileSnapshot
            {
                AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
                RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
                PolicyRevision = Required(options.PolicyRevision, nameof(options.PolicyRevision)),
                ActivationMode = AgentProfileActivationMode.Enforced,
                MaxPlanSteps = options.MaxPlanSteps,
                HandoffTtlSeconds = options.HandoffTtlSeconds,
                ClassifierTimeoutMs = options.ClassifierTimeoutMs,
                ExactSkillFetchTimeoutMs = options.ExactSkillFetchTimeoutMs,
                MaxSelectedSkillBytes = options.MaxSelectedSkillBytes,
                MaxOwnedToolCount = options.MaxOwnedToolCount,
                MaxSchemaBytes = options.MaxSchemaBytes,
                MaximumToolPolicy = CreatePolicy(options.MaximumToolPolicy),
                RecoveryToolPolicy = CreatePolicy(options.RecoveryToolPolicy),
                Instructions = Required(options.Instructions, nameof(options.Instructions)),
            },
        };

        draft.RuntimeProfile.Members.Add(options.Members.Select(CreateMember));
        var diagnostics = AgentProfilePolicies.ValidateDraft(draft);
        if (diagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "System NyxID chat Agent Profile draft is invalid: " +
                string.Join(", ", diagnostics.Select(static x => $"{x.Code}:{x.Field}")));
        }

        return AgentProfileDeterminism.NormalizeDraft(draft);
    }

    private static AgentProfileSkillMember CreateMember(AgentProfileSkillMemberOptions options)
    {
        var member = new AgentProfileSkillMember
        {
            IntentId = Required(options.IntentId, nameof(options.IntentId)),
            RoutingDescription = Normalize(options.RoutingDescription),
            SkillRef = new ExactRemoteSkillRef
            {
                Guid = Required(options.SkillGuid, nameof(options.SkillGuid)),
                LiteralVersion = Required(options.LiteralVersion, nameof(options.LiteralVersion)),
            },
            TaskToolPolicy = CreatePolicy(options.TaskToolPolicy),
            SideEffectClass = ParseSideEffectClass(options.SideEffectClass),
            ExpectedSkillName = Required(options.ExpectedSkillName, nameof(options.ExpectedSkillName)),
            ReviewedPublisherId = Required(options.ReviewedPublisherId, nameof(options.ReviewedPublisherId)),
        };
        member.ExplicitTriggerAliases.AddRange(options.ExplicitTriggerAliases.Select(Normalize).Where(static x => x.Length > 0));
        return member;
    }

    private static AgentProfileToolPolicy CreatePolicy(AgentProfileToolPolicyOptions? options)
    {
        var policy = new AgentProfileToolPolicy();
        if (options is null)
            return policy;

        policy.ToolNames.AddRange(options.ToolNames.Select(Normalize).Where(static x => x.Length > 0));
        policy.ToolSetRefs.AddRange(options.ToolSetRefs.Select(Normalize).Where(static x => x.Length > 0));
        policy.ConnectedServiceSelectors.Add(options.ConnectedServiceSelectors.Select(CreateSelector));
        policy.SelectReadOnlyConnectedOperations = options.SelectReadOnlyConnectedOperations;
        return policy;
    }

    private static AgentProfileConnectedServiceSelector CreateSelector(AgentProfileConnectedServiceSelectorOptions options)
    {
        var selector = new AgentProfileConnectedServiceSelector
        {
            CatalogServiceSlug = Required(options.CatalogServiceSlug, nameof(options.CatalogServiceSlug)),
            EndpointId = Normalize(options.EndpointId),
        };
        selector.AllowedRisks.AddRange(options.AllowedRisks.Select(AgentProfileRiskOptions.Parse));
        if (options.ReadinessRequestedScopes.Count > 0)
        {
            selector.Readiness = new AgentProfileConnectedServiceReadiness();
            selector.Readiness.RequestedScopes.AddRange(
                options.ReadinessRequestedScopes.Select(Normalize).Where(static x => x.Length > 0));
        }

        return selector;
    }

    private static AgentProfileSideEffectClass ParseSideEffectClass(string value) =>
        Normalize(value).ToLowerInvariant() switch
        {
            "read_only" or "readonly" or "read" => AgentProfileSideEffectClass.ReadOnly,
            "service_call" or "service" => AgentProfileSideEffectClass.ServiceCall,
            "maintenance" => AgentProfileSideEffectClass.Maintenance,
            _ => AgentProfileSideEffectClass.ExternalHandoff,
        };

    private static string Required(string value, string field)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0
            ? throw new InvalidOperationException($"System NyxID chat Agent Profile config requires {field}.")
            : normalized;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
