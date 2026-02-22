namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;

public sealed class MassTransitKafkaTransportOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";

    public string TopicName { get; set; } = OrleansRuntimeConstants.KafkaEventTopicName;

    public string ConsumerGroup { get; set; } = OrleansRuntimeConstants.KafkaDefaultConsumerGroup;
}
