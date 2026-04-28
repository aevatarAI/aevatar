using Aevatar.Foundation.Abstractions.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Replaces <see cref="IEventStore"/> with Garnet-backed persistence.
    /// </summary>
    public static IServiceCollection AddGarnetEventStore(
        this IServiceCollection services,
        Action<GarnetEventStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new GarnetEventStoreOptions();
        configure?.Invoke(options);
        ValidateOptions(options);

        services.Replace(ServiceDescriptor.Singleton(options));
        services.TryAddSingleton<IConnectionMultiplexer>(sp =>
        {
            var garnetOptions = sp.GetRequiredService<GarnetEventStoreOptions>();
            var connectionOptions = ConfigurationOptions.Parse(garnetOptions.ConnectionString);
            connectionOptions.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(connectionOptions);
        });
        services.Replace(ServiceDescriptor.Singleton<IEventStore, GarnetEventStore>());
        services.Replace(ServiceDescriptor.Singleton<IEventStoreMaintenance>(sp =>
            (IEventStoreMaintenance)sp.GetRequiredService<IEventStore>()));
        return services;
    }

    private static void ValidateOptions(GarnetEventStoreOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("GarnetEventStore requires a non-empty connection string.");
        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
            throw new InvalidOperationException("GarnetEventStore requires a non-empty key prefix.");
    }
}
