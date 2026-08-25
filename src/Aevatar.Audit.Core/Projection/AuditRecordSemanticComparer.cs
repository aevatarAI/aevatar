namespace Aevatar.Audit.Core.Projection;

public static class AuditRecordSemanticComparer
{
    public static bool AreEquivalent(Audit.AuditRecord first, Audit.AuditRecord second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return Normalize(first).Equals(Normalize(second));
    }

    private static Audit.AuditRecord Normalize(Audit.AuditRecord record)
    {
        var semanticRecord = record.Clone();
        semanticRecord.OccurredAt = null;
        semanticRecord.RecordedAt = null;
        if (semanticRecord.Correlation is not { } correlation)
            return semanticRecord;

        correlation.TraceId = string.Empty;
        correlation.SpanId = string.Empty;
        correlation.Traceparent = string.Empty;
        correlation.Tracestate = string.Empty;
        if (correlation.CalculateSize() == 0)
            semanticRecord.Correlation = null;

        return semanticRecord;
    }
}
