using Aevatar.Audit;
using Aevatar.Audit.Core.Projection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionAuditTrailArtifactStoreTests
{
    [Fact]
    public void AuditTrailArtifactStorageDocument_ShouldNotImplementProjectionReadModel()
    {
        typeof(AuditTrailArtifactStorageDocument)
            .GetInterfaces()
            .Should()
            .NotContain(typeof(IProjectionReadModel));
    }

    [Fact]
    public void FromArtifact_ShouldCloneAuditArtifactWithoutReadModelCompatibilityFields()
    {
        var document = CreateDocument("audit-1");

        var storageDocument = AuditTrailArtifactStorageDocument.FromArtifact(document);
        document.ScopeId = "mutated";

        storageDocument.Id.Should().Be("audit-1");
        storageDocument.UpdatedAtUtcValue!.ToDateTimeOffset()
            .Should()
            .Be(DateTimeOffset.Parse("2026-07-03T08:10:10+00:00"));
        storageDocument.Artifact.Should().NotBeSameAs(document);
        storageDocument.Artifact.ScopeId.Should().Be("scope-audit-1");
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
}
