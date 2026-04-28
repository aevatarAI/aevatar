namespace Aevatar.GAgents.Authoring.Lark;

/// <summary>
/// Single source of truth for the <c>agent_builder_action</c> identifiers wired between the
/// card-rendering surface (<see cref="AgentBuilderCardContent"/>) and the dispatch surfaces
/// (<see cref="AgentBuilderCardFlow"/>, <see cref="NyxRelayAgentBuilderFlow"/>).
/// </summary>
/// <remarks>
/// Keeping these in one place avoids the silent-divergence hazard of redeclaring the same string
/// literal in every consumer: a typo in a renderer's button argument would route the click to a
/// fallback branch with no compile-time signal. The card-flow router and the shared content
/// builders both reference the same constant by name.
/// </remarks>
internal static class AgentBuilderActionIds
{
    public const string DailyReport = "create_daily_report";
    public const string SocialMedia = "create_social_media";
    public const string OpenDailyReportForm = "open_daily_report_form";
    public const string OpenSocialMediaForm = "open_social_media_form";
    public const string ListTemplates = "list_templates";
    public const string ListAgents = "list_agents";
    public const string AgentStatus = "agent_status";
    public const string RunAgent = "run_agent";
    public const string DisableAgent = "disable_agent";
    public const string EnableAgent = "enable_agent";
    public const string ConfirmDeleteAgent = "confirm_delete_agent";
    public const string DeleteAgent = "delete_agent";
}
