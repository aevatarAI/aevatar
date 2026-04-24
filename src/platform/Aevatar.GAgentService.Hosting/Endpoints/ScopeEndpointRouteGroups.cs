using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgentService.Hosting.Endpoints;

internal static class ScopeEndpointRouteGroups
{
    public static RouteGroupBuilder MapScopeGroup(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/scopes");
        if (IsAuthenticationEnabled(app.ServiceProvider))
            group.RequireAuthorization();

        return group;
    }

    public static bool IsAuthenticationEnabled(IServiceProvider services)
    {
        return AevatarScopeAccessGuard.IsAuthenticationEnabled(services);
    }
}
