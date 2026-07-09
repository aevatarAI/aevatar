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

public enum WorkflowFileIngressPolicyRejectionKind
{
    Unspecified = 0,
    TooLarge = 1,
    UnsupportedMediaType = 2,
}

public sealed class WorkflowFileIngressPolicyException : Exception
{
    private WorkflowFileIngressPolicyException(
        WorkflowFileIngressPolicyRejectionKind kind,
        string message,
        string? fileName = null,
        string? mediaType = null,
        long? sizeBytes = null)
        : base(message)
    {
        Kind = kind;
        FileName = Normalize(fileName);
        MediaType = Normalize(mediaType);
        SizeBytes = sizeBytes;
    }

    public WorkflowFileIngressPolicyRejectionKind Kind { get; }

    public string? FileName { get; }

    public string? MediaType { get; }

    public long? SizeBytes { get; }

    public static WorkflowFileIngressPolicyException TooLarge(string? fileName, long sizeBytes) =>
        new(
            WorkflowFileIngressPolicyRejectionKind.TooLarge,
            $"{FormatFileName(fileName)}workflow file exceeds the ingress size policy ({sizeBytes} bytes).",
            fileName,
            sizeBytes: sizeBytes);

    public static WorkflowFileIngressPolicyException Unsupported(string? fileName, string? mediaType) =>
        new(
            WorkflowFileIngressPolicyRejectionKind.UnsupportedMediaType,
            $"{FormatFileName(fileName)}workflow file media type is not supported ({Normalize(mediaType) ?? "unknown media type"}).",
            fileName,
            mediaType);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatFileName(string? fileName)
    {
        var normalized = Normalize(fileName);
        return normalized is null ? string.Empty : $"'{normalized}' ";
    }
}

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
