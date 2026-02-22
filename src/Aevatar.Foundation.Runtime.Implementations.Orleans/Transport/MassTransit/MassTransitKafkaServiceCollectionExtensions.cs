using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

public static class MassTransitKafkaServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarFoundationRuntimeOrleansKafkaClientTransport(
        this IServiceCollection services,
        Action<OrleansKafkaTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = BuildOptions(configure);
        services.TryAddSingleton(options);

        services.AddMassTransit(x =>
        {
            x.UsingInMemory();

            x.AddRider(rider =>
            {
                rider.AddProducer<OrleansTransportEventMessage>(options.TopicName);
                rider.UsingKafka((_, kafka) => kafka.Host(options.BootstrapServers));
            });
        });

        services.TryAddSingleton<IOrleansTransportEventSender, KafkaOrleansTransportEventSender>();
        return services;
    }

    public static IServiceCollection AddAevatarFoundationRuntimeOrleansKafkaSiloTransport(
        this IServiceCollection services,
        Action<OrleansKafkaTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = BuildOptions(configure);
        services.TryAddSingleton(options);

        services.AddMassTransit(x =>
        {
            x.UsingInMemory();

            x.AddRider(rider =>
            {
                rider.AddConsumer<OrleansTransportEventConsumer>();
                rider.AddProducer<OrleansTransportEventMessage>(options.TopicName);

                rider.UsingKafka((context, kafka) =>
                {
                    kafka.Host(options.BootstrapServers);
                    kafka.TopicEndpoint<OrleansTransportEventMessage>(
                        options.TopicName,
                        options.ConsumerGroup,
                        endpoint =>
                        {
                            endpoint.ConfigureConsumer<OrleansTransportEventConsumer>(context);
                        });
                });
            });
        });

        services.TryAddSingleton<IOrleansTransportEventSender, KafkaOrleansTransportEventSender>();
        services.TryAddSingleton<IOrleansTransportEventHandler, OrleansTransportEventHandler>();
        return services;
    }

    private static OrleansKafkaTransportOptions BuildOptions(Action<OrleansKafkaTransportOptions>? configure)
    {
        var options = new OrleansKafkaTransportOptions();
        configure?.Invoke(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.BootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TopicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerGroup);

        return options;
    }
}
