// ─────────────────────────────────────────────────────────────
// AgentMetrics - runtime metrics for agent processing.
// Built on System.Diagnostics.Metrics Meter.
// ─────────────────────────────────────────────────────────────

using System.Diagnostics.Metrics;

namespace Aevatar.Foundation.Runtime.Observability;

/// <summary>Agent runtime metrics: events, handler duration, active actors.</summary>
public static class AgentMetrics
{
    private static readonly Meter Meter = new("Aevatar.Agents", "1.0.0");
    public const string RuntimeEnvelopeTerminalFailuresMetricName =
        "aevatar.runtime.envelope_terminal_failures_total";
    public const string DirectionTag = "direction";
    public const string ResultTag = "result";
    public const string FailureReasonTag = "failure_reason";
    public const string FailureDispositionTag = "failure_disposition";
    public const string ResultOk = "ok";
    public const string ResultError = "error";
    public const string FailureReasonHandlerRetryExhausted = "handler_retry_exhausted";
    public const string FailureReasonCompatibilityRetryExhausted = "compatibility_retry_exhausted";
    public const string FailureReasonActorUnavailable = "actor_unavailable";
    public const string FailureReasonInvalidEnvelope = "invalid_envelope";
    public const string FailureDispositionReturned = "returned";
    public const string FailureDispositionPropagated = "propagated";

    /// <summary>Total events handled by runtime actor pipelines.</summary>
    public static readonly Counter<long> RuntimeEventsHandled = Meter.CreateCounter<long>(
        "aevatar.runtime.events_handled",
        description: "Total number of runtime events handled.");

    /// <summary>Runtime event handle duration in milliseconds.</summary>
    public static readonly Histogram<double> RuntimeEventHandleDurationMs = Meter.CreateHistogram<double>(
        "aevatar.runtime.event_handle_duration_ms",
        description: "Runtime event handling duration in milliseconds.");

    /// <summary>Active actor count (up/down counter).</summary>
    public static readonly UpDownCounter<long> ActiveActors = Meter.CreateUpDownCounter<long>(
        "aevatar.runtime.active_actors",
        description: "Current number of active actors.");

    /// <summary>Runtime envelopes which reached an explicit terminal failure disposition.</summary>
    public static readonly Counter<long> RuntimeEnvelopeTerminalFailures = Meter.CreateCounter<long>(
        RuntimeEnvelopeTerminalFailuresMetricName,
        description: "Total runtime envelopes which reached a terminal failure disposition.");

    public static void RecordEventHandled(string direction, string result, double durationMs)
    {
        RuntimeEventsHandled.Add(1,
        [
            new(DirectionTag, direction),
            new(ResultTag, result),
        ]);
        RuntimeEventHandleDurationMs.Record(durationMs,
        [
            new(ResultTag, result),
        ]);
    }

    public static void RecordEnvelopeTerminalFailure(string reason, string failureDisposition)
    {
        RuntimeEnvelopeTerminalFailures.Add(1,
        [
            new(FailureReasonTag, reason),
            new(FailureDispositionTag, failureDisposition),
        ]);
    }
}
