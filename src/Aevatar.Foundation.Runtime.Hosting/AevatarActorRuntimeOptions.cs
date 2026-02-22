using Aevatar.Foundation.Runtime.Implementations.Orleans;

namespace Aevatar.Foundation.Runtime.Hosting;

public sealed class AevatarActorRuntimeOptions
{
    public const string SectionName = "ActorRuntime";
    public const string ProviderInMemory = "InMemory";
    public const string ProviderOrleans = "Orleans";
    public const string TransportInMemory = "InMemory";
    public const string TransportKafka = "Kafka";

    public string Provider { get; set; } = ProviderInMemory;

    public string Transport { get; set; } = TransportInMemory;

    public string KafkaBootstrapServers { get; set; } = "localhost:9092";

    public string KafkaTopicName { get; set; } = OrleansRuntimeConstants.KafkaEventTopicName;

    public string KafkaConsumerGroup { get; set; } = OrleansRuntimeConstants.KafkaDefaultConsumerGroup;
}
