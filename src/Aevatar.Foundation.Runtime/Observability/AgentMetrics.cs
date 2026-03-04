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
    public const string DirectionTag = "direction";
    public const string ResultTag = "result";
    public const string ResultOk = "ok";
    public const string ResultError = "error";

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
}
