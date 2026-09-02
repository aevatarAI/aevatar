namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record FileArtifactIngressRequest(
    ReadOnlyMemory<byte> Content,
    FileArtifactSourceKind SourceKind,
    string? SourceMessageId = null,
    string? SourceResourceKey = null,
    string? FileName = null,
    string? MediaType = null,
    long? ExpiresAtUnixMs = null,
    string? OwnerRunId = null,
    string? OwnerScopeId = null);

public sealed record FileArtifactIngressResult(FileArtifactRef FileRef);

public sealed record FileArtifactContent(FileArtifactRef FileRef, Stream Content);

public sealed record FileArtifactCleanupRequest(long ObservedAtUnixMs);

public sealed record FileArtifactCleanupResult(
    long ScannedArtifactCount,
    long DeletedExpiredArtifactCount,
    long DeletedIncompleteArtifactCount)
{
    public long DeletedArtifactCount => DeletedExpiredArtifactCount + DeletedIncompleteArtifactCount;
}

public interface IFileArtifactIngressPort
{
    ValueTask<FileArtifactIngressResult> IngestAsync(
        FileArtifactIngressRequest request,
        CancellationToken cancellationToken = default);
}

public interface IFileArtifactReadPort
{
    ValueTask<FileArtifactRef> DescribeAsync(
        FileArtifactRef fileRef,
        CancellationToken cancellationToken = default);

    ValueTask<FileArtifactContent> OpenReadAsync(
        FileArtifactRef fileRef,
        CancellationToken cancellationToken = default);
}

public interface IFileArtifactOwnershipPort
{
    ValueTask BindOwnerAsync(
        FileArtifactRef fileRef,
        string ownerRunId,
        string? ownerScopeId,
        CancellationToken cancellationToken = default);
}

public interface IFileArtifactCleanupPort
{
    ValueTask<FileArtifactCleanupResult> CleanupAsync(
        FileArtifactCleanupRequest request,
        CancellationToken cancellationToken = default);
}
