using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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
        var environment = services.GetService(typeof(IHostEnvironment)) as IHostEnvironment;
        var configuredValue = configuration?[AuthenticationEnabledKey];
        return ResolveAuthenticationEnabled(configuredValue, environment);
    }

    private static bool ResolveAuthenticationEnabled(string? configuredValue, IHostEnvironment? environment)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return true;

        if (!bool.TryParse(configuredValue, out var enabled))
            throw new InvalidOperationException(
                $"Invalid boolean value '{configuredValue}' for {AuthenticationEnabledKey}.");

        if (!enabled && environment is { } hostEnvironment && !hostEnvironment.IsDevelopment())
            return true;

        return enabled;
    }
}
