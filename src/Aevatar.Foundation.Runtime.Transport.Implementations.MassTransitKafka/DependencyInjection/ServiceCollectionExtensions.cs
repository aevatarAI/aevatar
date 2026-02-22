using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Confluent.Kafka;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarFoundationRuntimeMassTransitKafkaTransport(
        this IServiceCollection services,
        Action<MassTransitKafkaTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MassTransitKafkaTransportOptions();
        configure?.Invoke(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.BootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TopicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerGroup);

        services.TryAddSingleton(options);
        services.TryAddSingleton<MassTransitKafkaEnvelopeDispatcher>();

        services.AddMassTransit(x =>
        {
            x.UsingInMemory();

            x.AddRider(rider =>
            {
                rider.AddConsumer<MassTransitKafkaEnvelopeConsumer>();
                rider.AddProducer<KafkaStreamEnvelopeMessage>(options.TopicName);

                rider.UsingKafka((context, kafka) =>
                {
                    kafka.Host(options.BootstrapServers);
                    kafka.TopicEndpoint<KafkaStreamEnvelopeMessage>(
                        options.TopicName,
                        options.ConsumerGroup,
                        endpoint =>
                        {
                            endpoint.CreateIfMissing(_ => { });
                            endpoint.AutoOffsetReset = AutoOffsetReset.Earliest;
                            endpoint.ConfigureConsumer<MassTransitKafkaEnvelopeConsumer>(context);
                        });
                });
            });
        });

        services.TryAddSingleton<IMassTransitEnvelopeTransport, MassTransitKafkaEnvelopeTransport>();
        return services;
    }
}
