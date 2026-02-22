namespace Aevatar.Foundation.Runtime.Hosting;

public sealed class AevatarActorRuntimeOptions
{
    public const string SectionName = "ActorRuntime";
    public const string ProviderInMemory = "InMemory";
    public const string ProviderMassTransit = "MassTransit";
    public const string ProviderOrleans = "Orleans";
    public const string MassTransitTransportBackendKafka = "Kafka";
    public const string OrleansStreamBackendInMemory = "InMemory";
    public const string OrleansStreamBackendMassTransitAdapter = "MassTransitAdapter";
    public const string DefaultOrleansStreamProviderName = "AevatarOrleansStreamProvider";
    public const string DefaultOrleansActorEventNamespace = "aevatar.actor.events";

    public string Provider { get; set; } = ProviderInMemory;

    public string OrleansStreamBackend { get; set; } = OrleansStreamBackendInMemory;

    public string OrleansStreamProviderName { get; set; } = DefaultOrleansStreamProviderName;

    public string OrleansActorEventNamespace { get; set; } = DefaultOrleansActorEventNamespace;

    public string MassTransitTransportBackend { get; set; } = MassTransitTransportBackendKafka;

    public string MassTransitKafkaBootstrapServers { get; set; } = "localhost:9092";

    public string MassTransitKafkaTopicName { get; set; } = "aevatar-foundation-agent-events";

    public string MassTransitKafkaConsumerGroup { get; set; } = "aevatar-foundation-kafka-streaming";
}
