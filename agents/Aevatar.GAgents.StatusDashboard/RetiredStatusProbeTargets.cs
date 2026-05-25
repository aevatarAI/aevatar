namespace Aevatar.GAgents.StatusDashboard;

internal static class RetiredStatusProbeTargets
{
    public static readonly string[] Slugs =
    [
        "chat-completion-api-singular-route",
        "responses-api-auth-gate",
        "messages-api-auth-gate",
        "chat-completions-api-auth-gate",
        "models-api-auth-gate",
        "voice-websocket-auth-gate",
        "channel-registration-api-auth-gate",
        "nyxid-llm-status",
        "nyxid-llm-gateway-auth-gate",
        "nyxid-channel-bots-auth-gate",
        "nyxid-channel-relay-reply-auth-gate",
        "responses-forward-team-00-nyxid-identity",
        "responses-forward-team-01-nyxid-service",
        "responses-forward-team-02-nyxid-proxy-models",
        "responses-forward-team-03-direct-responses",
        "responses-forward-team-04-route-policy",
        "responses-forward-team-05-team-entry-member",
        "responses-forward-team-06-member-binding",
        "responses-forward-team-07-direct-team-invoke",
        "responses-forward-team-08-nyxid-proxy-e2e",
    ];

    public static bool Contains(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) &&
        Slugs.Contains(slug.Trim(), StringComparer.Ordinal);
}
