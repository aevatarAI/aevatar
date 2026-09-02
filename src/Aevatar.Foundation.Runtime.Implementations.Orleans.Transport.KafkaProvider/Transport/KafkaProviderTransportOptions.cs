using Confluent.Kafka;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

public sealed class KafkaProviderTransportOptions
{
    public const int DefaultReceiverBufferCapacity = 1024;
    public const int DefaultReceiverBufferHighWatermark = 768;
    public const int DefaultReceiverBufferLowWatermark = 512;

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string TopicName { get; set; } = "aevatar-foundation-agent-events";

    public string ConsumerGroup { get; set; } = "aevatar-foundation-kafka-streaming";

    public int TopicPartitionCount { get; set; } = 8;

    public TimeSpan MetadataTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// librdkafka statistics cadence. Set to <see cref="TimeSpan.Zero"/> to disable statistics;
    /// consumer-group lag will then be unavailable rather than reported as zero.
    /// </summary>
    public TimeSpan StatisticsInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hard upper bound for messages retained by one Kafka queue receiver.
    /// </summary>
    public int ReceiverBufferCapacity { get; set; } = DefaultReceiverBufferCapacity;

    /// <summary>
    /// Buffered message depth that pauses the receiver's assigned partition.
    /// </summary>
    public int ReceiverBufferHighWatermark { get; set; } = DefaultReceiverBufferHighWatermark;

    /// <summary>
    /// Buffered message depth that resumes a paused receiver partition.
    /// </summary>
    public int ReceiverBufferLowWatermark { get; set; } = DefaultReceiverBufferLowWatermark;

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

    internal void ValidateReceiverBufferWatermarks()
    {
        if (ReceiverBufferLowWatermark > 0 &&
            ReceiverBufferLowWatermark < ReceiverBufferHighWatermark &&
            ReceiverBufferHighWatermark <= ReceiverBufferCapacity)
        {
            return;
        }

        throw new InvalidOperationException(
            "Kafka receiver buffer watermarks require " +
            "0 < ReceiverBufferLowWatermark < ReceiverBufferHighWatermark <= ReceiverBufferCapacity, " +
            $"but low={ReceiverBufferLowWatermark}, high={ReceiverBufferHighWatermark}, " +
            $"capacity={ReceiverBufferCapacity}.");
    }
}
