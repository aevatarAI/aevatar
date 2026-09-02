using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

namespace Aevatar.Foundation.Runtime.Hosting;

public sealed class AevatarActorRuntimeOptions
{
    public const string SectionName = "ActorRuntime";
    public const string ProviderInMemory = "InMemory";
    public const string ProviderOrleans = "Orleans";
    public const string OrleansStreamBackendInMemory = "InMemory";
    public const string OrleansStreamBackendKafkaProvider = "KafkaProvider";
    public const string OrleansPersistenceBackendInMemory = "InMemory";
    public const string OrleansPersistenceBackendGarnet = "Garnet";
    public const string DefaultOrleansStreamProviderName = "AevatarOrleansStreamProvider";
    public const string DefaultOrleansActorEventNamespace = "aevatar.actor.events";
    public const string DefaultOrleansGarnetConnectionString = "localhost:6379";

    public string Provider { get; set; } = ProviderInMemory;

    public string OrleansStreamBackend { get; set; } = OrleansStreamBackendInMemory;

    public string OrleansStreamProviderName { get; set; } = DefaultOrleansStreamProviderName;

    public string OrleansActorEventNamespace { get; set; } = DefaultOrleansActorEventNamespace;

    public string OrleansPersistenceBackend { get; set; } = OrleansPersistenceBackendInMemory;

    public string OrleansGarnetConnectionString { get; set; } = DefaultOrleansGarnetConnectionString;

    public string SecretStoreBackend { get; set; } = string.Empty;

    public string SecretStoreConnectionString { get; set; } = string.Empty;

    public int SecretStoreDatabase { get; set; } = -1;

    public string SecretStoreKeyringPath { get; set; } = string.Empty;

    public string SecretStoreVaultPrefix { get; set; } = "aevatar:secret-vault";

    public string SecretStoreRuntimePrefix { get; set; } = "aevatar:runtime-secrets";

    public int OrleansQueueCount { get; set; } = 8;

    public int OrleansQueueCacheSize { get; set; } = 4096;

    public string KafkaBootstrapServers { get; set; } = "localhost:9092";

    public string KafkaTopicName { get; set; } = "aevatar-foundation-agent-events";

    public string KafkaConsumerGroup { get; set; } = "aevatar-foundation-kafka-streaming";

    public int KafkaReceiverBufferCapacity { get; set; } =
        KafkaProviderTransportOptions.DefaultReceiverBufferCapacity;

    public int KafkaReceiverBufferHighWatermark { get; set; } =
        KafkaProviderTransportOptions.DefaultReceiverBufferHighWatermark;

    public int KafkaReceiverBufferLowWatermark { get; set; } =
        KafkaProviderTransportOptions.DefaultReceiverBufferLowWatermark;

    public bool EventSourcingEnableSnapshots { get; set; } = true;

    public int EventSourcingSnapshotInterval { get; set; } = 200;

    public bool EventSourcingEnableEventCompaction { get; set; } = true;

    public int EventSourcingRetainedEventsAfterSnapshot { get; set; } = 0;
}
