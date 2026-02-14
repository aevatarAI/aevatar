// ─────────────────────────────────────────────────────────────
// MassTransitKafkaExtensions — Shared MassTransit + Kafka
// configuration for both Silo and Client processes.
//
// Silo:   Consumer + Producer + TopicEndpoint
// Client: Producer only (fire events to Kafka, Silo consumes)
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.Consumers;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Orleans.DependencyInjection;

/// <summary>
/// Shared MassTransit + Kafka (Rider) configuration extensions.
/// </summary>
public static class MassTransitKafkaExtensions
{
    /// <summary>
    /// Configures MassTransit + Kafka for the Silo process.
    /// Registers: AgentEventConsumer, ITopicProducer, TopicEndpoint,
    /// IAgentEventSender (KafkaAgentEventSender).
    /// </summary>
    public static IServiceCollection AddAevatarKafkaSilo(
        this IServiceCollection services,
        string kafkaBootstrap,
        string? topicName = null,
        string? consumerGroup = null)
    {
        var topic = topicName ?? Constants.AgentEventEndpoint;
        var group = consumerGroup ?? "aevatar-silo";

        services.AddMassTransit(x =>
        {
            // Base transport: in-memory (Kafka is a Rider, not a transport)
            x.UsingInMemory();

            x.AddRider(rider =>
            {
                rider.AddConsumer<AgentEventConsumer>();
                rider.AddProducer<AgentEventMessage>(topic);

                rider.UsingKafka((ctx, k) =>
                {
                    k.Host(kafkaBootstrap);
                    k.TopicEndpoint<AgentEventMessage>(topic, group, e =>
                    {
                        e.ConfigureConsumer<AgentEventConsumer>(ctx);
                    });
                });
            });
        });

        services.TryAddSingleton<IAgentEventSender, KafkaAgentEventSender>();
        return services;
    }

    /// <summary>
    /// Configures MassTransit + Kafka for the Client process.
    /// Registers: ITopicProducer (producer only, no consumer).
    /// IAgentEventSender (KafkaAgentEventSender).
    /// </summary>
    public static IServiceCollection AddAevatarKafkaClient(
        this IServiceCollection services,
        string kafkaBootstrap,
        string? topicName = null)
    {
        var topic = topicName ?? Constants.AgentEventEndpoint;

        services.AddMassTransit(x =>
        {
            x.UsingInMemory();

            x.AddRider(rider =>
            {
                // Client: producer only (no consumer, Silo consumes)
                rider.AddProducer<AgentEventMessage>(topic);

                rider.UsingKafka((_, k) =>
                {
                    k.Host(kafkaBootstrap);
                });
            });
        });

        services.TryAddSingleton<IAgentEventSender, KafkaAgentEventSender>();
        return services;
    }
}
