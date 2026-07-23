using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class UnavailableWorkflowFileMultipartUploadPolicyResolver
    : IWorkflowFileMultipartUploadPolicyResolver
{
    public ValueTask<WorkflowFileMultipartUploadPolicyResolution> ResolveAsync(
        WorkflowFileMultipartUploadCandidate candidate,
        FileArtifactRef descriptor,
        WorkflowFileMultipartUploadExecutionContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(WorkflowFileMultipartUploadPolicyResolution.Denied(
            "policy_unavailable",
            "workflow_file_submit multipart upload policy is unavailable."));
}

public sealed class UnavailableWorkflowFileMultipartUploadPort : IWorkflowFileMultipartUploadPort
{
    public ValueTask<WorkflowFileMultipartUploadResult> UploadAsync(
        WorkflowFileMultipartUploadRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(WorkflowFileMultipartUploadResult.Failure(
            "policy_unavailable",
            "workflow_file_submit multipart upload port is unavailable."));
}
