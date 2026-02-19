using Aevatar.Bootstrap.Hosting;
using Aevatar.Maker.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.Maker.Infrastructure.CapabilityApi;

public static class MakerCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddMakerCapability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddAevatarCapability(
            name: "maker",
            configureServices: static (services, configuration) => services.AddMakerCapability(configuration),
            mapEndpoints: static app => app.MapMakerCapabilityEndpoints());
    }
}
