using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static partial class AgentProfilePolicies
{
    public const string NyxIdChatAgentKind = "nyxid.chat";
    public const string NyxIdChatRouteToolSet = "agent-profile.nyxid-chat";
    public const int FullCohortBasisPoints = 10_000;
    private const int MaximumSlugLength = 63;
    private static readonly IReadOnlySet<string> SupportedRouteToolSetRefs =
        new HashSet<string>(StringComparer.Ordinal) { NyxIdChatRouteToolSet };

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
            if (!string.Equals(draft.RuntimeProfile.AgentKind, NyxIdChatAgentKind, StringComparison.Ordinal))
                diagnostics.Add(Diagnostic("UNSUPPORTED_AGENT_KIND", "runtimeProfile.agentKind", "Only nyxid.chat is supported."));
            if (!SupportedRouteToolSetRefs.Contains(draft.RuntimeProfile.RouteToolSetRef))
                diagnostics.Add(Diagnostic("UNSUPPORTED_ROUTE_TOOL_SET", "runtimeProfile.routeToolSetRef", "The route tool set is not registered for nyxid.chat."));
            foreach (var member in draft.RuntimeProfile.Members)
                diagnostics.AddRange(ValidateExactSkillReference(member));
        }

        return diagnostics;
    }

    public static bool IsSupportedAgentKind(string? agentKind) =>
        string.Equals(agentKind?.Trim(), NyxIdChatAgentKind, StringComparison.Ordinal);

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
