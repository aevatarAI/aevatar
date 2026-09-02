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
    internal const string OperationTag = "operation";
    internal const string PauseOperation = "pause";
    internal const string ResumeOperation = "resume";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Gauge<long> ConsumerGroupLag = Meter.CreateGauge<long>(
        "aevatar.kafka.consumer_group.lag",
        description: "Consumer-group transport lag reported by librdkafka statistics.");
    private static readonly Gauge<long> ReceiverBufferDepth = Meter.CreateGauge<long>(
        "aevatar.kafka.receiver.buffer_depth",
        description: "Messages currently buffered by the Orleans Kafka receiver.");
    private static readonly Gauge<long> ReceiverBufferCapacity = Meter.CreateGauge<long>(
        "aevatar.kafka.receiver.buffer_capacity",
        description: "Configured hard message capacity of the Orleans Kafka receiver buffer.");
    private static readonly Gauge<long> ReceiverPausedPartitions = Meter.CreateGauge<long>(
        "aevatar.kafka.receiver.paused_partitions",
        description: "Kafka partitions currently paused by receiver buffer backpressure.");
    private static readonly Counter<long> ReceiverPauseResume = Meter.CreateCounter<long>(
        "aevatar.kafka.receiver.pause_resume",
        description: "Kafka receiver partition pause and resume operations.");
    private static readonly Histogram<double> ReceiverPauseDuration = Meter.CreateHistogram<double>(
        "aevatar.kafka.receiver.pause_duration",
        unit: "ms",
        description: "Duration of Kafka receiver partition pause intervals.");
    private static readonly Counter<long> ReceiverBufferSaturations = Meter.CreateCounter<long>(
        "aevatar.kafka.receiver.buffer_saturations",
        description: "Kafka receiver transitions into high-watermark buffer saturation.");
    private static readonly Counter<long> ReceiverConsumeErrors = Meter.CreateCounter<long>(
        "aevatar.kafka.receiver.consume_errors",
        description: "Kafka receiver consume errors.");

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

    internal static void RecordReceiverBufferCapacity(
        string providerName,
        string topicName,
        int partitionId,
        int capacity) =>
        Record(() => ReceiverBufferCapacity.Record(
            Math.Max(0, capacity),
            Tags(providerName, topicName, partitionId)));

    internal static void RecordReceiverPausedPartitionCount(
        string providerName,
        string topicName,
        int partitionId,
        int pausedPartitionCount) =>
        Record(() => ReceiverPausedPartitions.Record(
            Math.Max(0, pausedPartitionCount),
            Tags(providerName, topicName, partitionId)));

    internal static void RecordReceiverPauseResume(
        string providerName,
        string topicName,
        int partitionId,
        string operation) =>
        Record(() => ReceiverPauseResume.Add(
            1,
            Tags(providerName, topicName, partitionId, operation)));

    internal static void RecordReceiverPauseDuration(
        string providerName,
        string topicName,
        int partitionId,
        TimeSpan duration) =>
        Record(() => ReceiverPauseDuration.Record(
            Math.Max(0, duration.TotalMilliseconds),
            Tags(providerName, topicName, partitionId)));

    internal static void RecordReceiverBufferSaturation(
        string providerName,
        string topicName,
        int partitionId) =>
        Record(() => ReceiverBufferSaturations.Add(
            1,
            Tags(providerName, topicName, partitionId)));

    internal static void RecordReceiverConsumeError(
        string providerName,
        string topicName,
        int partitionId) =>
        Record(() => ReceiverConsumeErrors.Add(
            1,
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

    private static TagList Tags(
        string providerName,
        string topicName,
        int partitionId,
        string operation)
    {
        var tags = Tags(providerName, topicName, partitionId);
        tags.Add(OperationTag, Normalize(operation));
        return tags;
    }

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
