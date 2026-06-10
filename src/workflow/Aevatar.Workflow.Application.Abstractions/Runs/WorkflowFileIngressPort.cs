namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowFileIngressRequest(
    ReadOnlyMemory<byte> Content,
    WorkflowFileSourceKind SourceKind,
    string? SourceMessageId = null,
    string? SourceResourceKey = null,
    string? FileName = null,
    string? MediaType = null,
    long? ExpiresAtUnixMs = null);

public sealed record WorkflowFileIngressResult(WorkflowFileRef FileRef);

public interface IWorkflowFileIngressPort
{
    ValueTask<WorkflowFileIngressResult> IngestAsync(
        WorkflowFileIngressRequest request,
        CancellationToken cancellationToken = default);
}
