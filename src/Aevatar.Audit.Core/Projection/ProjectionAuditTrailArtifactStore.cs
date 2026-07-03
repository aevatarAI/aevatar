using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Audit.Core.Projection;

public sealed class ProjectionAuditTrailArtifactStore : IAuditTrailArtifactStore
{
    private readonly IProjectionDocumentReader<AuditTrailArtifactStorageDocument, string> _reader;
    private readonly IProjectionDocumentWriter<AuditTrailArtifactStorageDocument> _writer;

    public ProjectionAuditTrailArtifactStore(
        IProjectionDocumentReader<AuditTrailArtifactStorageDocument, string> reader,
        IProjectionDocumentWriter<AuditTrailArtifactStorageDocument> writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public async Task<Audit.AuditTrailDocument?> GetAsync(string auditId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditId);

        var document = await _reader.GetAsync(auditId, ct);
        return document?.Artifact.Clone();
    }

    public async Task<AuditTrailArtifactWriteResult> UpsertAsync(
        Audit.AuditTrailDocument document,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var result = await _writer.UpsertAsync(ToStorageDocument(document), ct);
        return result.Disposition switch
        {
            ProjectionWriteDisposition.Applied => AuditTrailArtifactWriteResult.Applied(),
            ProjectionWriteDisposition.Duplicate => AuditTrailArtifactWriteResult.Duplicate(),
            ProjectionWriteDisposition.Stale => AuditTrailArtifactWriteResult.Duplicate(),
            ProjectionWriteDisposition.Conflict => AuditTrailArtifactWriteResult.Conflict(),
            ProjectionWriteDisposition.Gap => AuditTrailArtifactWriteResult.Conflict(),
            _ => AuditTrailArtifactWriteResult.Conflict(),
        };
    }

    private static AuditTrailArtifactStorageDocument ToStorageDocument(Audit.AuditTrailDocument document) =>
        new()
        {
            Id = document.AuditId,
            ActorId = document.AuditId,
            StateVersion = document.CommittedStateVersion,
            LastEventId = document.ContentHash,
            UpdatedAtUtcValue = document.UpdatedAt?.Clone(),
            Artifact = document.Clone(),
        };
}
