using Aevatar.AppPlatform.Hosting.DependencyInjection;
using Aevatar.AppPlatform.Hosting.Endpoints;
using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.AppPlatform.Hosting;

public static class AppPlatformCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddAppPlatformCapability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddAevatarCapability(
            "app-platform",
            static (services, configuration) => services.AddAppPlatformCapability(configuration),
            static app => app.MapAppPlatformEndpoints());
    }
}
