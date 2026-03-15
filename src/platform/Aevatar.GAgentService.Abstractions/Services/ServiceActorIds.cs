namespace Aevatar.GAgentService.Abstractions.Services;

public static class ServiceActorIds
{
    public static string Definition(ServiceIdentity identity) => Build("definition", identity);

    public static string RevisionCatalog(ServiceIdentity identity) => Build("revisions", identity);

    public static string Deployment(ServiceIdentity identity) => Build("deployment", identity);

    public static string ServingSet(ServiceIdentity identity) => Build("serving-set", identity);

    public static string Rollout(ServiceIdentity identity) => Build("rollout", identity);

    public static string BindingCatalog(ServiceIdentity identity) => Build("bindings", identity);

    public static string EndpointCatalog(ServiceIdentity identity) => Build("endpoint-catalog", identity);

    public static string PolicyCatalog(ServiceIdentity identity) => Build("policies", identity);

    private static string Build(string prefix, ServiceIdentity identity) =>
        $"gagent-service:{prefix}:{ServiceKeys.Build(identity)}";
}
