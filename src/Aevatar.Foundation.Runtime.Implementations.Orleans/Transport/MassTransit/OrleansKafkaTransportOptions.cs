namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

public sealed class OrleansKafkaTransportOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";

    public string TopicName { get; set; } = OrleansRuntimeConstants.KafkaEventTopicName;

    public string ConsumerGroup { get; set; } = OrleansRuntimeConstants.KafkaDefaultConsumerGroup;
}
