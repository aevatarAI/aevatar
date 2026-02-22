namespace Aevatar.Foundation.Runtime.Implementations.Orleans;

public static class OrleansRuntimeConstants
{
    public const string GrainStateStorageName = "aevatar-foundation-runtime-orleans";
    public const string DefaultOrleansStreamProviderName = "AevatarKafkaStreamProvider";
    public const string ActorEventStreamNamespace = "aevatar.actor.events";
    public const string KafkaEventTopicName = "aevatar-foundation-agent-events";
    public const string KafkaDefaultConsumerGroup = "aevatar-foundation-orleans-silo";
}
