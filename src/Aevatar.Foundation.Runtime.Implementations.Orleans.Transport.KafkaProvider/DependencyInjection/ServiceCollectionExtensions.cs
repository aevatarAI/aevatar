using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddSharedRegistrations(services);
        return services;
    }

    public static IServiceCollection AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(
        this IServiceCollection services,
        Action<KafkaProviderTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new KafkaProviderTransportOptions();
        configure(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.BootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TopicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerGroup);

        AddSharedRegistrations(services);
        services.RemoveAll<KafkaProviderTransportOptions>();
        services.AddSingleton(options);
        services.RemoveAll<KafkaProviderProducer>();
        services.AddSingleton<KafkaProviderProducer>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, KafkaProviderProducerHostedService>());
        return services;
    }

    public static ISiloBuilder AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(this ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport();
        });
        return builder;
    }

    private static void AddSharedRegistrations(IServiceCollection services)
    {
        services.TryAddSingleton(new KafkaProviderTransportOptions
        {
            BootstrapServers = "localhost:9092",
            TopicName = "aevatar-foundation-agent-events",
            ConsumerGroup = "aevatar-foundation-kafka-streaming",
            TopicPartitionCount = 8,
        });
        services.TryAddSingleton<IQueueAdapterFactory, KafkaProviderQueueAdapterFactory>();
    }
}
