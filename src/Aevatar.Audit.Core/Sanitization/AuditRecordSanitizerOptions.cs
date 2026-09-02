namespace Aevatar.Audit.Core.Sanitization;

public sealed class AuditRecordSanitizerOptions
{
    public int MaxSummaryLength { get; init; } = 512;

    public int MaxAnnotationKeyLength { get; init; } = 96;

    public int MaxAnnotationValueLength { get; init; } = 512;

    public int MaxAnnotations { get; init; } = 32;
}
