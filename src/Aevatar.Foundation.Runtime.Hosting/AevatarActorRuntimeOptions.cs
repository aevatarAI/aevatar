using Aevatar.Foundation.Runtime.Implementations.Orleans;

namespace Aevatar.Foundation.Runtime.Hosting;

public sealed class AevatarActorRuntimeOptions
{
    public const string SectionName = "ActorRuntime";
    public const string ProviderInMemory = "InMemory";
    public const string ProviderMassTransitKafka = "MassTransitKafka";
    public const string ProviderOrleans = "Orleans";
    public const string OrleansStreamBackendInMemory = "InMemory";
    public const string OrleansStreamBackendKafkaAdapter = "KafkaAdapter";

    public string Provider { get; set; } = ProviderInMemory;

    public string OrleansStreamBackend { get; set; } = OrleansStreamBackendInMemory;

    public string OrleansStreamProviderName { get; set; } = OrleansRuntimeConstants.DefaultOrleansStreamProviderName;

    public string OrleansActorEventNamespace { get; set; } = OrleansRuntimeConstants.ActorEventStreamNamespace;

    public string KafkaBootstrapServers { get; set; } = "localhost:9092";

    public string KafkaTopicName { get; set; } = OrleansRuntimeConstants.KafkaEventTopicName;

    public string KafkaConsumerGroup { get; set; } = OrleansRuntimeConstants.KafkaDefaultConsumerGroup;
}
