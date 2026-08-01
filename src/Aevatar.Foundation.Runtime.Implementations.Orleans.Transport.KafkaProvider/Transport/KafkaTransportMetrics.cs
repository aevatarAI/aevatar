using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

internal static class KafkaTransportMetrics
{
    internal const string MeterName = "Aevatar.Kafka.Transport";
    internal const string ProviderTag = "provider";
    internal const string TopicTag = "topic";
    internal const string PartitionTag = "partition";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Gauge<long> ConsumerGroupLag = Meter.CreateGauge<long>(
        "aevatar.kafka.consumer_group.lag",
        description: "Consumer-group transport lag reported by librdkafka statistics.");
    private static readonly Gauge<long> ReceiverBufferDepth = Meter.CreateGauge<long>(
        "aevatar.kafka.receiver.buffer_depth",
        description: "Messages currently buffered by the Orleans Kafka receiver.");

    internal static bool ObserveStatistics(
        string statisticsJson,
        string providerName,
        string topicName,
        int partitionId)
    {
        if (!TryReadConsumerLag(statisticsJson, topicName, partitionId, out var lag))
            return false;

        Record(() => ConsumerGroupLag.Record(lag, Tags(providerName, topicName, partitionId)));
        return true;
    }

    internal static void RecordReceiverBufferDepth(
        string providerName,
        string topicName,
        int partitionId,
        int depth) =>
        Record(() => ReceiverBufferDepth.Record(
            Math.Max(0, depth),
            Tags(providerName, topicName, partitionId)));

    internal static bool TryReadConsumerLag(
        string statisticsJson,
        string topicName,
        int partitionId,
        out long lag)
    {
        lag = 0;
        if (string.IsNullOrWhiteSpace(statisticsJson) || string.IsNullOrWhiteSpace(topicName))
            return false;

        try
        {
            using var document = JsonDocument.Parse(statisticsJson);
            if (!document.RootElement.TryGetProperty("topics", out var topics) ||
                !topics.TryGetProperty(topicName, out var topic) ||
                !topic.TryGetProperty("partitions", out var partitions) ||
                !partitions.TryGetProperty(partitionId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var partition) ||
                !partition.TryGetProperty("consumer_lag", out var consumerLag) ||
                !consumerLag.TryGetInt64(out lag) ||
                lag < 0)
            {
                lag = 0;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            lag = 0;
            return false;
        }
    }

    private static TagList Tags(string providerName, string topicName, int partitionId) =>
        new()
        {
            { ProviderTag, Normalize(providerName) },
            { TopicTag, Normalize(topicName) },
            { PartitionTag, partitionId },
        };

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private static void Record(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Transport metrics are observational and must not affect delivery.
            LogWarning(ex);
        }
    }

    private static void LogWarning(Exception exception) =>
        Trace.TraceWarning("Kafka transport metric emission failed: {0}", exception);
}
