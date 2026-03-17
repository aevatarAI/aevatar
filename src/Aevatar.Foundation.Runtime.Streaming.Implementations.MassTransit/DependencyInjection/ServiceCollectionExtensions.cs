using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarMassTransitStreamProvider(
        this IServiceCollection services,
        Action<MassTransitStreamOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MassTransitStreamOptions();
        configure?.Invoke(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.StreamNamespace);

        services.Replace(ServiceDescriptor.Singleton(options));
        services.Replace(ServiceDescriptor.Singleton<Aevatar.Foundation.Abstractions.IStreamProvider, MassTransitStreamProvider>());
        services.Replace(ServiceDescriptor.Singleton<IStreamLifecycleManager, MassTransitStreamLifecycleManager>());
        return services;
    }
}
