using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Observability;

public static class TracingContextHelpers
{
    public static void PopulateTraceId(EventEnvelope envelope, bool overwrite = false)
    {
        var activity = Activity.Current;
        if (activity == null)
            return;

        var trace = envelope.EnsurePropagation().EnsureTrace();
        SetTraceValue(() => trace.TraceId, value => trace.TraceId = value, activity.TraceId.ToString(), overwrite);
        SetTraceValue(() => trace.SpanId, value => trace.SpanId = value, activity.SpanId.ToString(), overwrite);
        SetTraceValue(
            () => trace.TraceFlags,
            value => trace.TraceFlags = value,
            ((byte)activity.ActivityTraceFlags).ToString("x2"),
            overwrite);
    }

    private static void SetTraceValue(
        Func<string> currentValue,
        Action<string> setValue,
        string? nextValue,
        bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(nextValue))
            return;

        if (!overwrite && !string.IsNullOrWhiteSpace(currentValue()))
            return;

        setValue(nextValue);
    }

    public static Dictionary<string, object?> CreateLogScopeState(EventEnvelope envelope) =>
        new()
        {
            ["trace_id"] = ResolveTraceId(envelope),
            ["correlation_id"] = envelope.Propagation?.CorrelationId ?? string.Empty,
            ["causation_id"] = ResolveCausationId(envelope),
        };

    public static IDisposable? BeginEnvelopeScope(ILogger logger, EventEnvelope envelope) =>
        logger.BeginScope(CreateLogScopeState(envelope));

    private static string ResolveTraceId(EventEnvelope envelope)
    {
        var traceId = envelope.Propagation?.Trace?.TraceId;
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            return traceId;
        }

        return Activity.Current?.TraceId.ToString() ?? string.Empty;
    }

    private static string ResolveCausationId(EventEnvelope envelope)
    {
        var causationId = envelope.Propagation?.CausationEventId;
        return !string.IsNullOrWhiteSpace(causationId)
            ? causationId
            : string.Empty;
    }
}
