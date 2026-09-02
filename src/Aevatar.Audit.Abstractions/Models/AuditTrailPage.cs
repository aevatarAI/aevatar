using Aevatar.Audit;

namespace Aevatar.Audit.Abstractions.Models;

public sealed record AuditTrailPage(
    IReadOnlyList<AuditRecord> Records,
    string? NextCursor,
    DateTimeOffset ReadAt,
    AuditQueryCoverage Coverage);

public sealed record AuditQueryCoverage(
    AuditQueryWindow RequestedWindow,
    AuditQueryWindow EffectiveWindow,
    bool Truncated,
    DateTimeOffset? IngestionWatermark,
    DateTimeOffset? CompleteThrough,
    AuditWindowCompleteness WindowCompleteness,
    AuditSchemaCompatibility SchemaCompatibility)
{
    public static AuditQueryCoverage Create(
        AuditTrailQuery query,
        bool truncated,
        DateTimeOffset? ingestionWatermark,
        DateTimeOffset? completeThrough,
        AuditSchemaCompatibility schemaCompatibility)
    {
        ArgumentNullException.ThrowIfNull(query);

        var requested = new AuditQueryWindow(ToUtc(query.OccurredFrom), ToUtc(query.OccurredTo));
        return new AuditQueryCoverage(
            requested,
            requested,
            truncated,
            ToUtc(ingestionWatermark),
            ToUtc(completeThrough),
            ResolveCompleteness(requested, ingestionWatermark, completeThrough),
            schemaCompatibility);
    }

    private static AuditWindowCompleteness ResolveCompleteness(
        AuditQueryWindow window,
        DateTimeOffset? ingestionWatermark,
        DateTimeOffset? completeThrough)
    {
        if (!window.From.HasValue || !window.To.HasValue)
            return AuditWindowCompleteness.Unbounded;

        if (completeThrough.HasValue && completeThrough.Value >= window.To.Value)
            return AuditWindowCompleteness.Complete;

        if (ingestionWatermark.HasValue && ingestionWatermark.Value < window.To.Value)
            return AuditWindowCompleteness.BehindIngestionWatermark;

        return AuditWindowCompleteness.Unknown;
    }

    private static DateTimeOffset? ToUtc(DateTimeOffset? value) => value?.ToUniversalTime();
}

public sealed record AuditQueryWindow(DateTimeOffset? From, DateTimeOffset? To);

public enum AuditWindowCompleteness
{
    Unknown = 0,
    Complete = 1,
    BehindIngestionWatermark = 2,
    Unbounded = 3,
}

public enum AuditSchemaCompatibility
{
    Current = 0,
    ContainsLegacyRecords = 1,
    Incompatible = 2,
}
