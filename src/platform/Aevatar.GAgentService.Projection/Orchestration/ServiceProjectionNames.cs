namespace Aevatar.GAgentService.Projection.Orchestration;

internal static class ServiceProjectionKinds
{
    public const string Catalog = "service-catalog";
    public const string Deployments = "service-deployments";
    public const string Revisions = "service-revisions";
    public const string Serving = "service-serving";
    public const string Rollouts = "service-rollouts";
    public const string Traffic = "service-traffic";
    public const string DraftRunSession = "service-draft-run-session";
}
