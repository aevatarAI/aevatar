using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.Runtime.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarActorRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AevatarActorRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AevatarActorRuntimeOptions();
        var configuredProvider = configuration[$"{AevatarActorRuntimeOptions.SectionName}:Provider"];
        if (!string.IsNullOrWhiteSpace(configuredProvider))
            options.Provider = configuredProvider;
        var configuredTransport = configuration[$"{AevatarActorRuntimeOptions.SectionName}:Transport"];
        if (!string.IsNullOrWhiteSpace(configuredTransport))
            options.Transport = configuredTransport;
        var configuredKafkaBootstrap = configuration[$"{AevatarActorRuntimeOptions.SectionName}:KafkaBootstrapServers"];
        if (!string.IsNullOrWhiteSpace(configuredKafkaBootstrap))
            options.KafkaBootstrapServers = configuredKafkaBootstrap;
        var configuredKafkaTopic = configuration[$"{AevatarActorRuntimeOptions.SectionName}:KafkaTopicName"];
        if (!string.IsNullOrWhiteSpace(configuredKafkaTopic))
            options.KafkaTopicName = configuredKafkaTopic;
        var configuredKafkaConsumerGroup = configuration[$"{AevatarActorRuntimeOptions.SectionName}:KafkaConsumerGroup"];
        if (!string.IsNullOrWhiteSpace(configuredKafkaConsumerGroup))
            options.KafkaConsumerGroup = configuredKafkaConsumerGroup;
        var configuredOrleansStreamBackend = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamBackend"];
        if (!string.IsNullOrWhiteSpace(configuredOrleansStreamBackend))
            options.OrleansStreamBackend = configuredOrleansStreamBackend;
        var configuredOrleansStreamProvider = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamProviderName"];
        if (!string.IsNullOrWhiteSpace(configuredOrleansStreamProvider))
            options.OrleansStreamProviderName = configuredOrleansStreamProvider;
        var configuredOrleansActorNamespace = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansActorEventNamespace"];
        if (!string.IsNullOrWhiteSpace(configuredOrleansActorNamespace))
            options.OrleansActorEventNamespace = configuredOrleansActorNamespace;
        configure?.Invoke(options);

        services.Replace(ServiceDescriptor.Singleton(options));

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderInMemory, StringComparison.OrdinalIgnoreCase))
        {
            services.AddAevatarRuntime();
            return services;
        }

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderMassTransitKafka, StringComparison.OrdinalIgnoreCase))
        {
            services.AddAevatarRuntime();
            services.AddAevatarFoundationRuntimeMassTransitKafkaTransport(transportOptions =>
            {
                transportOptions.BootstrapServers = options.KafkaBootstrapServers;
                transportOptions.TopicName = options.KafkaTopicName;
                transportOptions.ConsumerGroup = options.KafkaConsumerGroup;
            });
            services.AddAevatarMassTransitKafkaStreamProvider();
            return services;
        }

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
        {
            services.AddAevatarRuntime();
            services.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
            {
                orleansOptions.StreamBackend = options.OrleansStreamBackend;
                orleansOptions.StreamProviderName = options.OrleansStreamProviderName;
                orleansOptions.ActorEventNamespace = options.OrleansActorEventNamespace;
            });

            if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendInMemory, StringComparison.OrdinalIgnoreCase))
                return services;

            if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendKafkaAdapter, StringComparison.OrdinalIgnoreCase))
            {
                services.AddAevatarFoundationRuntimeMassTransitKafkaTransport(transportOptions =>
                {
                    transportOptions.BootstrapServers = options.KafkaBootstrapServers;
                    transportOptions.TopicName = options.KafkaTopicName;
                    transportOptions.ConsumerGroup = options.KafkaConsumerGroup;
                });
                services.AddAevatarOrleansStreamProviderAdapter();
                return services;
            }

            throw new InvalidOperationException(
                $"Unsupported Orleans stream backend '{options.OrleansStreamBackend}'.");
        }

        throw new InvalidOperationException(
            $"Unsupported ActorRuntime provider '{options.Provider}'.");
    }
}
