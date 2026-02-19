using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosting;

public static class AevatarCapabilityHostExtensions
{
    public static WebApplicationBuilder AddAevatarCapability(
        this WebApplicationBuilder builder,
        string name,
        Action<IServiceCollection, IConfiguration> configureServices,
        Action<IEndpointRouteBuilder>? mapEndpoints = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureServices);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        configureServices(builder.Services, builder.Configuration);

        if (mapEndpoints is null)
            return builder;

        builder.Services.AddSingleton(new AevatarCapabilityRegistration
        {
            Name = name,
            MapEndpoints = mapEndpoints,
        });

        return builder;
    }

    public static IEndpointRouteBuilder MapAevatarCapabilities(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var registrations = app.ServiceProvider.GetServices<AevatarCapabilityRegistration>();
        foreach (var registration in registrations)
            registration.MapEndpoints(app);

        return app;
    }
}
