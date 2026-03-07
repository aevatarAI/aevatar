using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

internal static class AGUIEventEnvelopeMappingHelpers
{
    public static long? ToUnixMs(Timestamp? ts)
    {
        if (ts == null)
            return null;

        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
    }

    public static string ResolveThreadId(EventEnvelope envelope, string fallback)
    {
        return string.IsNullOrWhiteSpace(envelope.PublisherId)
            ? fallback
            : envelope.PublisherId;
    }

    public static string ResolveRunId(EventEnvelope envelope, string fallbackThreadId)
    {
        return string.IsNullOrWhiteSpace(envelope.CorrelationId)
            ? fallbackThreadId
            : envelope.CorrelationId;
    }

    public static string ResolveMessageId(string? sessionId, string? envelopeId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"msg:{sessionId}";

        return $"msg:{envelopeId}";
    }

    public static string ResolveRoleFromPublisher(string? publisherId)
    {
        if (string.IsNullOrWhiteSpace(publisherId))
            return "assistant";

        var normalized = publisherId.Trim();
        var idx = normalized.LastIndexOf(':');
        if (idx >= 0 && idx < normalized.Length - 1)
            return normalized[(idx + 1)..];

        return normalized;
    }
}
