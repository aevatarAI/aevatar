using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class GAgentServiceCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddGAgentServiceCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddAevatarCapability(
            "gagent-service",
            static (services, configuration) => services.AddGAgentServiceCapability(configuration),
            static app => app.MapGAgentServiceEndpoints());
    }
}
