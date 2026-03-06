namespace Aevatar.Foundation.Abstractions.Propagation;

/// <summary>
/// Reserved envelope metadata keys managed by framework-level propagation.
/// </summary>
/// <remarks>
/// Two key namespaces are used:
/// - <c>trace.*</c>  — OpenTelemetry / observability keys. Written by tracing helpers;
///   read by log scope builders and span parent-restoration logic.
/// - <c>__*</c>      — Internal runtime keys. Written by publishers; used by routing
///   metrics and debugging tools. Not intended for inter-service propagation.
/// </remarks>
public static class EnvelopeMetadataKeys
{
    // ── OpenTelemetry trace keys (trace.*) ──────────────────────────────────

    /// <summary>
    /// OpenTelemetry trace id used for cross-runtime log/trace correlation.
    /// </summary>
    public const string TraceId = "trace.trace_id";

    /// <summary>
    /// OpenTelemetry span id used for parent-child trace hierarchy.
    /// </summary>
    public const string TraceSpanId = "trace.span_id";

    /// <summary>
    /// OpenTelemetry trace flags (hex) to preserve upstream sampling semantics.
    /// </summary>
    public const string TraceFlags = "trace.flags";

    /// <summary>
    /// Direct upstream event id for one-hop causation link.
    /// </summary>
    public const string TraceCausationId = "trace.causation_id";

    // ── Internal runtime keys (__*) ─────────────────────────────────────────

    /// <summary>
    /// Number of route targets for fan-out metrics.
    /// </summary>
    public const string RouteTargetCount = "__route_target_count";

    /// <summary>
    /// Actor id that originally created/published the envelope.
    /// </summary>
    public const string SourceActorId = "__source_actor_id";
}
