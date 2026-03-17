using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgentService.Governance.Hosting.Endpoints;

public static class GAgentServiceGovernanceEndpoints
{
    public static RouteGroupBuilder MapGAgentServiceGovernanceEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        ServiceBindingEndpoints.Map(group);
        ServiceEndpointCatalogEndpoints.Map(group);
        ServicePolicyEndpoints.Map(group);
        return group;
    }
}
