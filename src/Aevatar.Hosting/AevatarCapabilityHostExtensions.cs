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

        var existingRegistration = FindRegistration(builder.Services, name);
        if (existingRegistration != null)
        {
            if (mapEndpoints != null && !AreEquivalent(existingRegistration.MapEndpoints, mapEndpoints))
            {
                throw new InvalidOperationException(
                    $"Capability '{name}' is already registered with a different endpoint mapper.");
            }

            return builder;
        }

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
        var mappedCapabilityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registrations)
        {
            if (!mappedCapabilityNames.Add(registration.Name))
            {
                throw new InvalidOperationException(
                    $"Capability '{registration.Name}' is registered more than once.");
            }

            registration.MapEndpoints(app);
        }

        return app;
    }

    private static AevatarCapabilityRegistration? FindRegistration(IServiceCollection services, string name)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(AevatarCapabilityRegistration))
                continue;

            if (descriptor.ImplementationInstance is not AevatarCapabilityRegistration registration)
                continue;

            if (string.Equals(registration.Name, name, StringComparison.OrdinalIgnoreCase))
                return registration;
        }

        return null;
    }

    private static bool AreEquivalent(
        Action<IEndpointRouteBuilder> left,
        Action<IEndpointRouteBuilder> right) =>
        Equals(left.Method, right.Method) &&
        ReferenceEquals(left.Target, right.Target);
}
