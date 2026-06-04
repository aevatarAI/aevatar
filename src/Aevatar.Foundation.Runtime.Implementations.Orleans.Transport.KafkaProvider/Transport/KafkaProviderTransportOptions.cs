using Confluent.Kafka;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

public sealed class KafkaProviderTransportOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";

    public string TopicName { get; set; } = "aevatar-foundation-agent-events";

    public string ConsumerGroup { get; set; } = "aevatar-foundation-kafka-streaming";

    public int TopicPartitionCount { get; set; } = 8;

    public TimeSpan MetadataTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum size (bytes) of a single produced message, applied to the librdkafka producer.
    /// Actor event envelopes can legitimately carry large payloads (e.g. aggregated tool-call
    /// results from a skill run), so this default raises the producer ceiling above the ~1 MB
    /// librdkafka default to keep the producer from rejecting them locally before compression.
    /// The broker still enforces its own max.message.bytes against the compressed batch.
    /// </summary>
    public int ProducerMaxMessageBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Compression codec applied to produced batches. Defaults to Gzip so large envelopes compress
    /// under the broker's max.message.bytes limit without requiring any broker-side configuration
    /// change; brokers store the codec per batch, so existing uncompressed records stay readable.
    /// </summary>
    public CompressionType ProducerCompressionType { get; set; } = CompressionType.Gzip;
}
