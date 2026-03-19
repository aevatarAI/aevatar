using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddSharedRegistrations(services);
        return services;
    }

    public static IServiceCollection AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport(
        this IServiceCollection services,
        Action<KafkaPartitionAwareTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new KafkaPartitionAwareTransportOptions();
        configure(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.BootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TopicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerGroup);

        AddSharedRegistrations(services);
        services.RemoveAll<KafkaPartitionAwareTransportOptions>();
        services.AddSingleton(options);
        services.RemoveAll<KafkaPartitionAwareEnvelopeTransport>();
        services.RemoveAll<IKafkaPartitionAwareEnvelopeTransport>();
        services.AddSingleton<KafkaPartitionAwareEnvelopeTransport>();
        services.AddSingleton<IKafkaPartitionAwareEnvelopeTransport>(
            sp => sp.GetRequiredService<KafkaPartitionAwareEnvelopeTransport>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, KafkaPartitionLifecycleBridgeHostedService>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, KafkaPartitionAwareTransportHostedService>());
        return services;
    }

    public static ISiloBuilder AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport(this ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport();
        });
        return builder;
    }

    private static void AddSharedRegistrations(IServiceCollection services)
    {
        services.TryAddSingleton<LocalPartitionRecordRouter>();
        services.TryAddSingleton<ILocalDeliveryAckPort>(sp => sp.GetRequiredService<LocalPartitionRecordRouter>());
        services.TryAddSingleton<IPartitionOwnedReceiverFactory, KafkaPartitionOwnedReceiverFactory>();
        services.TryAddSingleton<IPartitionOwnedReceiverRegistry, PartitionOwnedReceiverRegistry>();
        services.TryAddSingleton<IPartitionAssignmentManager, KafkaPartitionAssignmentManager>();
        services.TryAddSingleton<KafkaAssignedPartitionQueueBalancer>();
        services.TryAddSingleton<IQueueAdapterFactory, KafkaPartitionAwareQueueAdapterFactory>();
    }
}
