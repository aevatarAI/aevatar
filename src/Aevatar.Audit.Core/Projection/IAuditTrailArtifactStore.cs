namespace Aevatar.Audit.Core.Projection;

public interface IAuditTrailArtifactStore
{
    Task<Audit.AuditTrailDocument?> GetAsync(string auditId, CancellationToken ct = default);

    Task<AuditTrailArtifactWriteResult> UpsertAsync(Audit.AuditTrailDocument document, CancellationToken ct = default);
}

public enum AuditTrailArtifactWriteDisposition
{
    Applied = 0,
    Duplicate = 1,
    Conflict = 2,
}

public readonly record struct AuditTrailArtifactWriteResult(AuditTrailArtifactWriteDisposition Disposition)
{
    public static AuditTrailArtifactWriteResult Applied() => new(AuditTrailArtifactWriteDisposition.Applied);

    public static AuditTrailArtifactWriteResult Duplicate() => new(AuditTrailArtifactWriteDisposition.Duplicate);

    public static AuditTrailArtifactWriteResult Conflict() => new(AuditTrailArtifactWriteDisposition.Conflict);
}
