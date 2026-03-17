using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarFoundationRuntimeOrleansMassTransitAdapter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IQueueAdapterFactory, OrleansMassTransitQueueAdapterFactory>();
        return services;
    }

    public static ISiloBuilder AddAevatarFoundationRuntimeOrleansMassTransitAdapter(this ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.AddAevatarFoundationRuntimeOrleansMassTransitAdapter();
        });
        return builder;
    }
}
