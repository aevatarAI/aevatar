namespace Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;

public sealed class MassTransitKafkaTransportOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";

    public string TopicName { get; set; } = "aevatar-foundation-agent-events";

    public string ConsumerGroup { get; set; } = "aevatar-foundation-kafka-streaming";
}
