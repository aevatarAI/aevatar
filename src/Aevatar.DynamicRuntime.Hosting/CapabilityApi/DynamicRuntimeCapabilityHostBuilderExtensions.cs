using Aevatar.DynamicRuntime.Infrastructure;
using Aevatar.DynamicRuntime.Projection;
using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.DynamicRuntime.Hosting.CapabilityApi;

public static class DynamicRuntimeCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddDynamicRuntimeCapability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddAevatarCapability(
            name: "dynamic-runtime",
            configureServices: static (services, _) =>
            {
                services.AddDynamicRuntimeProjection();
                services.AddDynamicRuntime();
            },
            mapEndpoints: static app => app.MapDynamicRuntimeCapabilityEndpoints());
    }
}
