namespace Aevatar.GAgentService.Projection.Orchestration;

internal static class ServiceProjectionKinds
{
    public const string Catalog = "service-catalog";
    public const string Deployments = "service-deployments";
    public const string Revisions = "service-revisions";
    public const string Serving = "service-serving";
    public const string Rollouts = "service-rollouts";
    public const string Traffic = "service-traffic";
    public const string InvocationCatalog = "service-invocation-catalog";
    public const string DraftRunSession = "service-draft-run-session";
    public const string ScriptServiceAguiSession = "script-service-agui-session";
    public const string LlmSessionObservation = "llm-session-observation";
    public const string Runs = "service-runs";
    public const string GAgentRunTerminalDraftRun = "gagent-run-terminal-draft-run";
    public const string GAgentRunTerminalApproval = "gagent-run-terminal-approval";
    public const string ResponseSessions = "response-sessions";
    public const string ResponsesAgentTools = "responses-agent-tools";
    public const string ScheduledDispatches = "scheduled-dispatches";
    public const string TeamAutomationOperationObservation = "team-automation-operation-observation";
    public const string NyxIdAuthorizationCatalog = "nyxid-authorization-catalog";
    public const string NyxIdAuthorizationCatalogRefreshObservation =
        "nyxid-authorization-catalog-refresh-observation";
    public const string AgentProfileCatalog = "agent-profile-catalog";
    public const string AgentProfileCurrentState = "agent-profile-current-state";
}
