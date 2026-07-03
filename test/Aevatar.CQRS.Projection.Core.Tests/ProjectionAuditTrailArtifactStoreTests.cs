using Aevatar.Audit;
using Aevatar.Audit.Core.Projection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionAuditTrailArtifactStoreTests
{
    [Fact]
    public async Task GetAsync_ShouldReturnClonedArtifactFromProjectionReader()
    {
        var artifact = CreateDocument("audit-1");
        var reader = new RecordingDocumentReader
        {
            Document = new AuditTrailArtifactStorageDocument
            {
                Id = "audit-1",
                Artifact = artifact,
            },
        };
        var store = new ProjectionAuditTrailArtifactStore(reader, new RecordingDocumentWriter());

        var first = await store.GetAsync("audit-1");
        first!.ScopeId = "mutated";
        var second = await store.GetAsync("audit-1");

        reader.LastKey.Should().Be("audit-1");
        first.Should().NotBeSameAs(artifact);
        second.Should().NotBeSameAs(first);
        second!.ScopeId.Should().Be("scope-audit-1");
        artifact.ScopeId.Should().Be("scope-audit-1");
    }

    [Fact]
    public async Task UpsertAsync_ShouldMapAuditDocumentToProjectionStorageDocument()
    {
        var writer = new RecordingDocumentWriter();
        var store = new ProjectionAuditTrailArtifactStore(new RecordingDocumentReader(), writer);
        var document = CreateDocument("audit-1");

        var result = await store.UpsertAsync(document);
        document.ScopeId = "mutated";

        result.Disposition.Should().Be(AuditTrailArtifactWriteDisposition.Applied);
        var storageDocument = writer.Upserts.Should().ContainSingle().Subject;
        storageDocument.Id.Should().Be("audit-1");
        storageDocument.ActorId.Should().Be("audit-1");
        storageDocument.StateVersion.Should().Be(42);
        storageDocument.LastEventId.Should().Be("content-audit-1");
        storageDocument.UpdatedAtUtcValue!.ToDateTimeOffset()
            .Should()
            .Be(DateTimeOffset.Parse("2026-07-03T08:10:10+00:00"));
        storageDocument.Artifact.Should().NotBeSameAs(document);
        storageDocument.Artifact.ScopeId.Should().Be("scope-audit-1");
    }

    [Theory]
    [InlineData(ProjectionWriteDisposition.Applied, AuditTrailArtifactWriteDisposition.Applied)]
    [InlineData(ProjectionWriteDisposition.Duplicate, AuditTrailArtifactWriteDisposition.Duplicate)]
    [InlineData(ProjectionWriteDisposition.Stale, AuditTrailArtifactWriteDisposition.Duplicate)]
    [InlineData(ProjectionWriteDisposition.Conflict, AuditTrailArtifactWriteDisposition.Conflict)]
    [InlineData(ProjectionWriteDisposition.Gap, AuditTrailArtifactWriteDisposition.Conflict)]
    public async Task UpsertAsync_ShouldMapProjectionWriteDisposition(
        ProjectionWriteDisposition projectionDisposition,
        AuditTrailArtifactWriteDisposition auditDisposition)
    {
        var writer = new RecordingDocumentWriter
        {
            Result = new ProjectionWriteResult(projectionDisposition),
        };
        var store = new ProjectionAuditTrailArtifactStore(new RecordingDocumentReader(), writer);

        var result = await store.UpsertAsync(CreateDocument("audit-1"));

        result.Disposition.Should().Be(auditDisposition);
    }

    private static AuditTrailDocument CreateDocument(string auditId) =>
        new()
        {
            Id = auditId,
            AuditId = auditId,
            ContentHash = $"content-{auditId}",
            Record = new AuditRecord
            {
                AuditId = auditId,
                ScopeId = $"scope-{auditId}",
            },
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T08:09:10+00:00")),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T08:10:10+00:00")),
            ScopeId = $"scope-{auditId}",
            CommittedStateVersion = 42,
        };

    private sealed class RecordingDocumentReader
        : IProjectionDocumentReader<AuditTrailArtifactStorageDocument, string>
    {
        public AuditTrailArtifactStorageDocument? Document { get; init; }

        public string LastKey { get; private set; } = "";

        public Task<AuditTrailArtifactStorageDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastKey = key;
            return Task.FromResult(Document);
        }

        public Task<ProjectionDocumentQueryResult<AuditTrailArtifactStorageDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingDocumentWriter
        : IProjectionDocumentWriter<AuditTrailArtifactStorageDocument>
    {
        public ProjectionWriteResult Result { get; init; } = ProjectionWriteResult.Applied();

        public List<AuditTrailArtifactStorageDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            AuditTrailArtifactStorageDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(Result);
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
