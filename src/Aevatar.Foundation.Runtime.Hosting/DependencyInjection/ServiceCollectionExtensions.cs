using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;
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
        configure?.Invoke(options);

        services.Replace(ServiceDescriptor.Singleton(options));

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderInMemory, StringComparison.OrdinalIgnoreCase))
        {
            services.AddAevatarRuntime();
            return services;
        }

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
        {
            services.AddAevatarRuntime();
            services.AddAevatarFoundationRuntimeOrleans();

            if (string.Equals(options.Transport, AevatarActorRuntimeOptions.TransportInMemory, StringComparison.OrdinalIgnoreCase))
                return services;

            if (string.Equals(options.Transport, AevatarActorRuntimeOptions.TransportKafka, StringComparison.OrdinalIgnoreCase))
            {
                services.AddAevatarFoundationRuntimeOrleansKafkaClientTransport(transportOptions =>
                {
                    transportOptions.BootstrapServers = options.KafkaBootstrapServers;
                    transportOptions.TopicName = options.KafkaTopicName;
                    transportOptions.ConsumerGroup = options.KafkaConsumerGroup;
                });
                return services;
            }

            throw new InvalidOperationException(
                $"Unsupported Orleans transport '{options.Transport}'.");
        }

        throw new InvalidOperationException(
            $"Unsupported ActorRuntime provider '{options.Provider}'.");
    }
}
