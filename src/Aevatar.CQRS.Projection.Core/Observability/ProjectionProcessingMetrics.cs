using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aevatar.CQRS.Projection.Core.Observability;

internal static class ProjectionProcessingMetrics
{
    internal const string MeterName = "Aevatar.CQRS.Projection";
    internal const string ProjectionKindTag = "projection.kind";
    internal const string EventKindTag = "event.kind";
    internal const string MaterializerKindTag = "materializer.kind";
    internal const string ResultTag = "result";
    internal const string MaterializerDurationMetricName = "aevatar.projection.materializer.duration";
    internal const string MaterializerTotalMetricName = "aevatar.projection.materializer.total";
    internal const string ResultCompleted = "completed";
    internal const string ResultFailed = "failed";
    internal const string ResultCancelled = "cancelled";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> ReceivedEnvelopes = Meter.CreateCounter<long>(
        "aevatar.projection.envelope.received",
        description: "Projection envelopes received by an active scope.");
    private static readonly Counter<long> AttemptedEnvelopes = Meter.CreateCounter<long>(
        "aevatar.projection.envelope.attempted",
        description: "Projection envelopes attempted after the successful-version fence.");
    private static readonly Counter<long> SuccessfulMaterializations = Meter.CreateCounter<long>(
        "aevatar.projection.materialization.succeeded",
        description: "Successful projection materializations.");
    private static readonly Counter<long> FailedAttempts = Meter.CreateCounter<long>(
        "aevatar.projection.materialization.failed",
        description: "Failed initial or replay materialization attempts.");
    private static readonly Counter<long> RetryExhausted = Meter.CreateCounter<long>(
        "aevatar.projection.retry.exhausted",
        description: "Projection failures that reached the replay retry limit.");
    private static readonly UpDownCounter<long> UnresolvedFailureChanges = Meter.CreateUpDownCounter<long>(
        "aevatar.projection.unresolved_failure.change",
        description: "Process-lifetime changes in unresolved projection repair failures.");
    private static readonly Histogram<double> OldestUnresolvedFailureAge = Meter.CreateHistogram<double>(
        "aevatar.projection.oldest_unresolved_failure.age",
        unit: "s",
        description: "Age of the oldest unresolved projection failure when the backlog changes.");
    private static readonly Histogram<double> MaterializationLatency = Meter.CreateHistogram<double>(
        "aevatar.projection.materialization.latency",
        unit: "ms",
        description: "Elapsed time for a successful projection materialization attempt.");
    private static readonly Histogram<double> MaterializerDuration = Meter.CreateHistogram<double>(
        MaterializerDurationMetricName,
        unit: "ms",
        description: "Elapsed time for one concrete projection materializer invocation.");
    private static readonly Counter<long> MaterializerTotal = Meter.CreateCounter<long>(
        MaterializerTotalMetricName,
        description: "Concrete projection materializer terminal outcomes.");
    private static readonly Counter<long> DiagnosticFailuresDropped = Meter.CreateCounter<long>(
        "aevatar.projection.failure_diagnostic.dropped",
        description: "Payload-free failure diagnostics dropped from the bounded retention ring.");

    internal static void RecordReceived(string projectionKind, string eventKind) =>
        Record(() => ReceivedEnvelopes.Add(1, Tags(projectionKind, eventKind)));

    internal static void RecordAttempted(string projectionKind, string eventKind) =>
        Record(() => AttemptedEnvelopes.Add(1, Tags(projectionKind, eventKind)));

    internal static void RecordSucceeded(string projectionKind, string eventKind, TimeSpan elapsed) =>
        Record(() =>
        {
            var tags = Tags(projectionKind, eventKind);
            SuccessfulMaterializations.Add(1, tags);
            MaterializationLatency.Record(Math.Max(0, elapsed.TotalMilliseconds), tags);
        });

    internal static void RecordFailed(
        string projectionKind,
        string eventKind,
        int unresolvedCount,
        DateTimeOffset? oldestOccurredAt,
        bool addsUnresolvedFailure,
        bool retryExhausted = false)
    {
        Record(() =>
        {
            var tags = Tags(projectionKind, eventKind);
            FailedAttempts.Add(1, tags);
            if (retryExhausted)
                RetryExhausted.Add(1, tags);
            RecordBacklogSample(projectionKind, unresolvedCount, oldestOccurredAt, delta: addsUnresolvedFailure ? 1 : 0);
        });
    }

    internal static void RecordResolved(
        string projectionKind,
        int unresolvedCount,
        DateTimeOffset? oldestOccurredAt) =>
        Record(() => RecordBacklogSample(projectionKind, unresolvedCount, oldestOccurredAt, delta: -1));

    internal static void RecordDiagnosticDropped(string projectionKind, int count) =>
        Record(() => DiagnosticFailuresDropped.Add(
            Math.Max(0, count),
            new KeyValuePair<string, object?>(ProjectionKindTag, Normalize(projectionKind))));

    internal static void RecordMaterializerTerminal(
        string projectionKind,
        string materializerKind,
        string result,
        TimeSpan elapsed)
    {
        Record(() => MaterializerTotal.Add(
            1,
            MaterializerTags(projectionKind, materializerKind, result)));
        Record(() => MaterializerDuration.Record(
            Math.Max(0, elapsed.TotalMilliseconds),
            MaterializerTags(projectionKind, materializerKind, result)));
    }

    private static void RecordBacklogSample(
        string projectionKind,
        int unresolvedCount,
        DateTimeOffset? oldestOccurredAt,
        int delta)
    {
        var tag = new KeyValuePair<string, object?>(ProjectionKindTag, Normalize(projectionKind));
        if (delta != 0)
            UnresolvedFailureChanges.Add(delta, tag);

        if (unresolvedCount > 0 && oldestOccurredAt.HasValue)
        {
            OldestUnresolvedFailureAge.Record(
                Math.Max(0, (DateTimeOffset.UtcNow - oldestOccurredAt.Value).TotalSeconds),
                tag);
        }
    }

    private static TagList Tags(string projectionKind, string eventKind) =>
        new()
        {
            { ProjectionKindTag, Normalize(projectionKind) },
            { EventKindTag, Normalize(eventKind) },
        };

    private static TagList MaterializerTags(
        string projectionKind,
        string materializerKind,
        string result) =>
        new()
        {
            { ProjectionKindTag, Normalize(projectionKind) },
            { MaterializerKindTag, Normalize(materializerKind) },
            { ResultTag, NormalizeResult(result) },
        };

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private static string NormalizeResult(string result) =>
        result switch
        {
            ResultCompleted => ResultCompleted,
            ResultCancelled => ResultCancelled,
            _ => ResultFailed,
        };

    private static void Record(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Metrics are observations and must never change projection behavior.
            LogWarning(ex);
        }
    }

    private static void LogWarning(Exception exception)
    {
        try
        {
            Trace.TraceWarning(
                "Projection metric emission failed. errorType={0}",
                exception.GetType().Name);
        }
        catch (Exception)
        {
            return;
        }
    }
}
