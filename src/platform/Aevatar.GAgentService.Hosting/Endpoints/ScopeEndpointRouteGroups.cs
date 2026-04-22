using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Aevatar.GAgentService.Hosting.Endpoints;

internal static class ScopeEndpointRouteGroups
{
    private const string AuthenticationEnabledKey = "Aevatar:Authentication:Enabled";

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
        ArgumentNullException.ThrowIfNull(services);

        var configuration = services.GetService(typeof(IConfiguration)) as IConfiguration;
        var configuredValue = configuration?[AuthenticationEnabledKey];
        return !bool.TryParse(configuredValue, out var enabled) || enabled;
    }
}
