using System.Diagnostics;
using Aevatar.Foundation.Abstractions.Propagation;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Observability;

public static class TracingContextHelpers
{
    public static void PopulateTraceId(EventEnvelope envelope, bool overwrite = false)
    {
        var activity = Activity.Current;
        if (activity == null)
            return;

        SetMetadata(envelope, EnvelopeMetadataKeys.TraceId, activity.TraceId.ToString(), overwrite);
        SetMetadata(envelope, EnvelopeMetadataKeys.TraceSpanId, activity.SpanId.ToString(), overwrite);
        SetMetadata(envelope, EnvelopeMetadataKeys.TraceFlags, ((byte)activity.ActivityTraceFlags).ToString("x2"), overwrite);
    }

    private static void SetMetadata(EventEnvelope envelope, string key, string? value, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!overwrite && envelope.Metadata.ContainsKey(key))
            return;

        envelope.Metadata[key] = value;
    }

    public static Dictionary<string, object?> CreateLogScopeState(EventEnvelope envelope) =>
        new()
        {
            ["trace_id"] = ResolveTraceId(envelope),
            ["correlation_id"] = envelope.CorrelationId ?? string.Empty,
            ["causation_id"] = ResolveCausationId(envelope),
        };

    public static IDisposable? BeginEnvelopeScope(ILogger logger, EventEnvelope envelope) =>
        logger.BeginScope(CreateLogScopeState(envelope));

    private static string ResolveTraceId(EventEnvelope envelope)
    {
        if (envelope.Metadata.TryGetValue(EnvelopeMetadataKeys.TraceId, out var traceId) &&
            !string.IsNullOrWhiteSpace(traceId))
        {
            return traceId;
        }

        return Activity.Current?.TraceId.ToString() ?? string.Empty;
    }

    private static string ResolveCausationId(EventEnvelope envelope) =>
        envelope.Metadata.TryGetValue(EnvelopeMetadataKeys.TraceCausationId, out var causationId) &&
        !string.IsNullOrWhiteSpace(causationId)
            ? causationId
            : string.Empty;
}
