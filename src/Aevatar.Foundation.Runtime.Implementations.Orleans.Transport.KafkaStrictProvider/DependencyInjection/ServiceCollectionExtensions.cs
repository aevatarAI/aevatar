using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarFoundationRuntimeOrleansKafkaStrictProviderTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddSharedRegistrations(services);
        return services;
    }

    public static IServiceCollection AddAevatarFoundationRuntimeOrleansKafkaStrictProviderTransport(
        this IServiceCollection services,
        Action<KafkaStrictProviderTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new KafkaStrictProviderTransportOptions();
        configure(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.BootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TopicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerGroup);

        AddSharedRegistrations(services);
        services.RemoveAll<KafkaStrictProviderTransportOptions>();
        services.AddSingleton(options);
        services.RemoveAll<KafkaStrictProviderEnvelopeTransport>();
        services.RemoveAll<IKafkaStrictProviderEnvelopeTransport>();
        services.AddSingleton<KafkaStrictProviderEnvelopeTransport>();
        services.AddSingleton<IKafkaStrictProviderEnvelopeTransport>(
            sp => sp.GetRequiredService<KafkaStrictProviderEnvelopeTransport>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, KafkaStrictProviderTransportHostedService>());
        return services;
    }

    public static ISiloBuilder AddAevatarFoundationRuntimeOrleansKafkaStrictProviderTransport(this ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.AddAevatarFoundationRuntimeOrleansKafkaStrictProviderTransport();
        });
        return builder;
    }

    private static void AddSharedRegistrations(IServiceCollection services)
    {
        services.TryAddSingleton(new KafkaStrictProviderTransportOptions
        {
            BootstrapServers = "localhost:9092",
            TopicName = "aevatar-foundation-agent-events",
            ConsumerGroup = "aevatar-foundation-kafka-streaming",
            TopicPartitionCount = 8,
        });
        services.TryAddSingleton<IQueueAdapterFactory, KafkaStrictProviderQueueAdapterFactory>();
    }
}
