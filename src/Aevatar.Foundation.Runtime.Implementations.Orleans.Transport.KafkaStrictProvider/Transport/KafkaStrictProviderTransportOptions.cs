namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider;

public sealed class KafkaStrictProviderTransportOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";

    public string TopicName { get; set; } = "aevatar-foundation-agent-events";

    public string ConsumerGroup { get; set; } = "aevatar-foundation-kafka-streaming";

    public int TopicPartitionCount { get; set; } = 8;
}
