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

    /// <summary>Processed event counter.</summary>
    public static readonly Counter<long> EventsHandled = Meter.CreateCounter<long>("aevatar.agent.events_handled");

    /// <summary>Handler duration histogram in milliseconds.</summary>
    public static readonly Histogram<double> HandlerDuration = Meter.CreateHistogram<double>("aevatar.agent.handler_duration_ms");

    /// <summary>End-to-end event handle duration in milliseconds.</summary>
    public static readonly Histogram<double> EventHandleDuration = Meter.CreateHistogram<double>("aevatar.agent.event_handle_duration_ms");

    /// <summary>Published route target count by direction.</summary>
    public static readonly Counter<long> RouteTargets = Meter.CreateCounter<long>("aevatar.runtime.route_targets");

    /// <summary>State store load counter.</summary>
    public static readonly Counter<long> StateLoads = Meter.CreateCounter<long>("aevatar.state.loads");

    /// <summary>State store save counter.</summary>
    public static readonly Counter<long> StateSaves = Meter.CreateCounter<long>("aevatar.state.saves");

    /// <summary>Active actor count (up/down counter).</summary>
    public static readonly UpDownCounter<long> ActiveActors = Meter.CreateUpDownCounter<long>("aevatar.runtime.active_actors");
}
