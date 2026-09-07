using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static partial class AgentProfilePolicies
{
    public const string NyxIdChatAgentKind = "nyxid.chat";
    public const string WorkspaceChatAgentKind = "workspace.chat";
    public const string ChannelReplyAgentKind = "channel.reply";
    public const string NyxIdChatRouteToolSet = "agent-profile.nyxid-chat";
    public const string WorkspaceChatRouteToolSet = "workspace.default";
    public const string ChannelReplyRouteToolSet = "workspace.default";
    public const int CanaryCohortBasisPoints = 500;
    public const int ExpandedCohortBasisPoints = 2_500;
    public const int FullCohortBasisPoints = 10_000;
    private const int MaximumSlugLength = 63;
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> SupportedRouteToolSetRefs =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [NyxIdChatAgentKind] = new HashSet<string>(StringComparer.Ordinal) { NyxIdChatRouteToolSet },
            [WorkspaceChatAgentKind] = new HashSet<string>(StringComparer.Ordinal) { WorkspaceChatRouteToolSet },
            [ChannelReplyAgentKind] = new HashSet<string>(StringComparer.Ordinal) { ChannelReplyRouteToolSet },
        };

    public static IReadOnlyList<AgentProfileDiagnostic> ValidateProfileSlug(string? profileSlug)
    {
        if (!string.IsNullOrEmpty(profileSlug) &&
            profileSlug.Length <= MaximumSlugLength &&
            ProfileSlugPattern().IsMatch(profileSlug))
        {
            return [];
        }

        return
        [
            new AgentProfileDiagnostic
            {
                Code = "INVALID_PROFILE_SLUG",
                Field = "profileSlug",
                Message = "Profile slug must use lowercase letters, digits, and single hyphens.",
            },
        ];
    }

    public static IReadOnlyList<AgentProfileDiagnostic> ValidateExactSkillReference(
        AgentProfileSkillMember? member)
    {
        if (member?.SkillRef is null ||
            !Guid.TryParseExact(member.SkillRef.Guid, "D", out _) ||
            !string.Equals(member.SkillRef.Guid, member.SkillRef.Guid.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return [Diagnostic("INVALID_SKILL_GUID", "skillRef.guid", "Exact skill GUID is required.")];
        }

        if (!LiteralVersionPattern().IsMatch(member.SkillRef.LiteralVersion ?? string.Empty))
        {
            return
            [
                Diagnostic(
                    "INVALID_LITERAL_VERSION",
                    "skillRef.literalVersion",
                    "Literal skill version must use major.minor form."),
            ];
        }

        if (string.IsNullOrWhiteSpace(member.ExpectedSkillName))
            return [Diagnostic("EXPECTED_SKILL_NAME_REQUIRED", "expectedSkillName", "Expected skill name is required.")];

        if (string.IsNullOrWhiteSpace(member.ReviewedPublisherId))
            return [Diagnostic("PUBLISHER_REQUIRED", "reviewedPublisherId", "Reviewed publisher id is required.")];

        return [];
    }

    public static IReadOnlyList<AgentProfileDiagnostic> ValidateDraft(AgentProfileDraft? draft)
    {
        var diagnostics = new List<AgentProfileDiagnostic>();
        if (draft is null)
            return [Diagnostic("DRAFT_REQUIRED", "draft", "Profile draft is required.")];

        if (string.IsNullOrWhiteSpace(draft.DisplayName))
            diagnostics.Add(Diagnostic("DISPLAY_NAME_REQUIRED", "displayName", "Display name is required."));
        if (string.IsNullOrWhiteSpace(draft.Instructions))
            diagnostics.Add(Diagnostic("INSTRUCTIONS_REQUIRED", "instructions", "Instructions are required."));
        if (draft.RuntimeProfile is null)
            diagnostics.Add(Diagnostic("RUNTIME_PROFILE_REQUIRED", "runtimeProfile", "Runtime profile is required."));
        else
        {
            if (!SupportedRouteToolSetRefs.TryGetValue(draft.RuntimeProfile.AgentKind, out var routeToolSets))
                diagnostics.Add(Diagnostic("UNSUPPORTED_AGENT_KIND", "runtimeProfile.agentKind", "The profile agent kind is not supported."));
            else if (!routeToolSets.Contains(draft.RuntimeProfile.RouteToolSetRef))
                diagnostics.Add(Diagnostic("UNSUPPORTED_ROUTE_TOOL_SET", "runtimeProfile.routeToolSetRef", "The route tool set is not registered for the profile agent kind."));
            diagnostics.AddRange(ValidateToolPolicy(
                draft.RuntimeProfile.MaximumToolPolicy,
                "runtimeProfile.maximumToolPolicy"));
            diagnostics.AddRange(ValidateToolPolicy(
                draft.RuntimeProfile.RecoveryToolPolicy,
                "runtimeProfile.recoveryToolPolicy"));
            var intentIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < draft.RuntimeProfile.Members.Count; index++)
            {
                var member = draft.RuntimeProfile.Members[index];
                var intentId = member.IntentId?.Trim() ?? string.Empty;
                if (intentId.Length == 0)
                {
                    diagnostics.Add(Diagnostic(
                        "PROFILE_INTENT_ID_REQUIRED",
                        $"runtimeProfile.members[{index}].intentId",
                        "Profile member intent id is required."));
                }
                else if (!intentIds.Add(intentId))
                {
                    diagnostics.Add(Diagnostic(
                        "PROFILE_INTENT_ID_DUPLICATE",
                        $"runtimeProfile.members[{index}].intentId",
                        $"Profile member intent id '{intentId}' must be unique."));
                }

                diagnostics.AddRange(ValidateExactSkillReference(member));
                diagnostics.AddRange(ValidateToolPolicy(
                    member.TaskToolPolicy,
                    $"runtimeProfile.members[{index}].taskToolPolicy"));
            }
        }

        return diagnostics;
    }

    public static bool IsSupportedAgentKind(string? agentKind) =>
        !string.IsNullOrWhiteSpace(agentKind) && SupportedRouteToolSetRefs.ContainsKey(agentKind.Trim());

    public static bool IsSupportedRouteToolSet(string? agentKind, string? routeToolSetRef) =>
        !string.IsNullOrWhiteSpace(agentKind) &&
        !string.IsNullOrWhiteSpace(routeToolSetRef) &&
        SupportedRouteToolSetRefs.TryGetValue(agentKind.Trim(), out var routeToolSets) &&
        routeToolSets.Contains(routeToolSetRef.Trim());

    public static bool IsReviewedRolloutCohort(int cohortBasisPoints) =>
        cohortBasisPoints is
            CanaryCohortBasisPoints or
            ExpandedCohortBasisPoints or
            FullCohortBasisPoints;

    private static IReadOnlyList<AgentProfileDiagnostic> ValidateToolPolicy(
        AgentProfileToolPolicy? policy,
        string field)
    {
        if (policy is null || policy.ConnectedServiceSelectors.Count == 0)
            return [];

        var diagnostics = new List<AgentProfileDiagnostic>();
        var seenSelectors = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < policy.ConnectedServiceSelectors.Count; index++)
        {
            var selector = policy.ConnectedServiceSelectors[index];
            var selectorField = $"{field}.connectedServiceSelectors[{index}]";
            if (!NyxIdServiceSlugPolicy.IsCanonical(selector.CatalogServiceSlug) &&
                !IsDynamicReadConnectedServiceSelector(selector) &&
                !IsEndpointOnlyReadConnectedServiceSelector(selector))
            {
                diagnostics.Add(Diagnostic(
                    "PROFILE_CONNECTED_SERVICE_SLUG_INVALID",
                    $"{selectorField}.catalogServiceSlug",
                    "Connected-service catalog slug must be canonical, or empty for read-only dynamic or endpoint-only selection."));
            }
            else if (!seenSelectors.Add(SelectorKey(selector)))
            {
                diagnostics.Add(Diagnostic(
                    "PROFILE_CONNECTED_SERVICE_SELECTOR_DUPLICATE",
                    selectorField,
                    "A tool policy may contain only one selector for each catalog service and endpoint pair."));
            }

            if (!IsValidEndpointId(selector.EndpointId))
            {
                diagnostics.Add(Diagnostic(
                    "PROFILE_CONNECTED_SERVICE_ENDPOINT_INVALID",
                    $"{selectorField}.endpointId",
                    "Connected-service endpoint id must be normalized and contain at most 256 non-control characters."));
            }

            if (selector.AllowedRisks.Count == 0)
            {
                diagnostics.Add(Diagnostic(
                    "PROFILE_CONNECTED_SERVICE_RISKS_REQUIRED",
                    $"{selectorField}.allowedRisks",
                    "A connected-service selector must allow READ_ONLY and/or WRITE."));
            }
            else if (selector.AllowedRisks.Any(static risk =>
                         risk is not (AgentToolOperationRiskPayload.ReadOnly or
                             AgentToolOperationRiskPayload.Write)))
            {
                diagnostics.Add(Diagnostic(
                    "PROFILE_CONNECTED_SERVICE_RISK_INVALID",
                    $"{selectorField}.allowedRisks",
                    "Connected-service selector risks may contain only READ_ONLY and WRITE."));
            }

            if (selector.Readiness is not null)
            {
                var scopes = selector.Readiness.RequestedScopes;
                if (scopes.Count == 0 ||
                    scopes.Count > 64 ||
                    scopes.Any(static scope =>
                        string.IsNullOrWhiteSpace(scope) ||
                        !string.Equals(scope, scope.Trim(), StringComparison.Ordinal) ||
                        scope.Length > 256 ||
                        scope.Any(char.IsControl)) ||
                    scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Count)
                {
                    diagnostics.Add(Diagnostic(
                        "PROFILE_CONNECTED_SERVICE_READINESS_SCOPES_INVALID",
                        $"{selectorField}.readiness.requestedScopes",
                        "Readiness scopes must be a non-empty distinct normalized set of at most 64 values."));
                }
            }
        }

        return diagnostics;
    }

    private static string SelectorKey(AgentProfileConnectedServiceSelector selector) =>
        string.Concat(selector.CatalogServiceSlug, "\0", selector.EndpointId);

    private static bool IsDynamicReadConnectedServiceSelector(AgentProfileConnectedServiceSelector selector) =>
        string.IsNullOrEmpty(selector.CatalogServiceSlug) &&
        string.IsNullOrEmpty(selector.EndpointId) &&
        selector.Readiness is null &&
        selector.AllowedRisks.Count == 1 &&
        selector.AllowedRisks[0] == AgentToolOperationRiskPayload.ReadOnly;

    private static bool IsEndpointOnlyReadConnectedServiceSelector(AgentProfileConnectedServiceSelector selector) =>
        string.IsNullOrEmpty(selector.CatalogServiceSlug) &&
        !string.IsNullOrEmpty(selector.EndpointId) &&
        selector.Readiness is null &&
        selector.AllowedRisks.Count == 1 &&
        selector.AllowedRisks[0] == AgentToolOperationRiskPayload.ReadOnly;

    private static bool IsValidEndpointId(string? endpointId) =>
        string.IsNullOrEmpty(endpointId) ||
        endpointId.Length <= 256 &&
        string.Equals(endpointId, endpointId.Trim(), StringComparison.Ordinal) &&
        !endpointId.Any(char.IsControl);

    private static AgentProfileDiagnostic Diagnostic(string code, string field, string message) =>
        new()
        {
            Code = code,
            Field = field,
            Message = message,
        };

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileSlugPattern();

    [GeneratedRegex("^(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralVersionPattern();
}
