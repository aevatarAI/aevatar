namespace Aevatar.GAgents.StatusDashboard;

internal static class RetiredStatusProbeTargets
{
    public static readonly string[] Slugs =
    [
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
