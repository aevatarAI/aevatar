namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowFileIngressRequest(
    ReadOnlyMemory<byte> Content,
    WorkflowFileSourceKind SourceKind,
    string? SourceMessageId = null,
    string? SourceResourceKey = null,
    string? FileName = null,
    string? MediaType = null,
    long? ExpiresAtUnixMs = null,
    string? OwnerRunId = null,
    string? OwnerScopeId = null);

public sealed record WorkflowFileIngressResult(WorkflowFileRef FileRef);

public sealed record WorkflowFileArtifactContent(WorkflowFileRef FileRef, Stream Content);

public sealed record WorkflowFileArtifactCleanupRequest(long ObservedAtUnixMs);

public sealed record WorkflowFileArtifactCleanupResult(
    long ScannedArtifactCount,
    long DeletedExpiredArtifactCount,
    long DeletedIncompleteArtifactCount)
{
    public long DeletedArtifactCount => DeletedExpiredArtifactCount + DeletedIncompleteArtifactCount;
}

public interface IWorkflowFileIngressPort
{
    ValueTask<WorkflowFileIngressResult> IngestAsync(
        WorkflowFileIngressRequest request,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowFileArtifactReadPort
{
    ValueTask<WorkflowFileRef> DescribeAsync(
        WorkflowFileRef fileRef,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowFileArtifactContent> OpenReadAsync(
        WorkflowFileRef fileRef,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowFileArtifactOwnershipPort
{
    ValueTask BindOwnerAsync(
        WorkflowFileRef fileRef,
        string ownerRunId,
        string? ownerScopeId,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowFileArtifactCleanupPort
{
    ValueTask<WorkflowFileArtifactCleanupResult> CleanupAsync(
        WorkflowFileArtifactCleanupRequest request,
        CancellationToken cancellationToken = default);
}
