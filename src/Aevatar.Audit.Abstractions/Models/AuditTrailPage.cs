using Aevatar.Audit;

namespace Aevatar.Audit.Abstractions.Models;

public sealed record AuditTrailPage(
    IReadOnlyList<AuditRecord> Records,
    string? NextCursor,
    DateTimeOffset ReadAt,
    DateTimeOffset? Watermark);
