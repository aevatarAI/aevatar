namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowFileMultipartUploadExecutionContext(
    string RunId,
    string? ParentRunId,
    string? RootRunId,
    string ScopeId,
    string StepId,
    string ExecutionId,
    string CallId,
    string IdempotencyKey);

public sealed record WorkflowFileMultipartUploadCandidate(
    FileArtifactRef FileRef,
    string ServiceSlug,
    string Path,
    string Method,
    string FileFieldName,
    IReadOnlyDictionary<string, string> FormFields,
    string? OutputKind,
    string? OutputSelector,
    long? MaxFileBytes);

public sealed record WorkflowFileMultipartUploadPolicy(
    string ServiceSlug,
    string Path,
    string Method,
    string FileFieldName,
    IReadOnlyDictionary<string, string> FormFields,
    string OutputKind,
    string OutputSelector,
    long MaxFileBytes);

public sealed record WorkflowFileMultipartUploadPolicyResolution(
    bool IsAllowed,
    WorkflowFileMultipartUploadPolicy? Policy,
    string? Error,
    string? Detail)
{
    public static WorkflowFileMultipartUploadPolicyResolution Allowed(
        WorkflowFileMultipartUploadPolicy policy) =>
        new(true, policy, null, null);

    public static WorkflowFileMultipartUploadPolicyResolution Denied(
        string error,
        string detail) =>
        new(false, null, error, detail);
}

public sealed record WorkflowFileMultipartUploadRequest(
    WorkflowCallerCredential CallerCredential,
    string ServiceSlug,
    string Path,
    string Method,
    string FileFieldName,
    IReadOnlyDictionary<string, string> FormFields,
    string FileName,
    string MediaType,
    long SizeBytes,
    string? Sha256,
    string OutputSelector,
    Stream Content);

public sealed record WorkflowFileMultipartUploadResult(
    bool Succeeded,
    string? OutputCode = null,
    string? Error = null,
    string? Detail = null,
    int? ProviderCode = null,
    int? HttpStatus = null)
{
    public static WorkflowFileMultipartUploadResult Success(
        string outputCode,
        int? providerCode = null,
        int? httpStatus = null) =>
        new(true, outputCode, null, null, providerCode, httpStatus);

    public static WorkflowFileMultipartUploadResult Failure(
        string error,
        string detail,
        int? providerCode = null,
        int? httpStatus = null) =>
        new(false, null, error, detail, providerCode, httpStatus);
}

public interface IWorkflowFileMultipartUploadPolicyResolver
{
    ValueTask<WorkflowFileMultipartUploadPolicyResolution> ResolveAsync(
        WorkflowFileMultipartUploadCandidate candidate,
        FileArtifactRef descriptor,
        WorkflowFileMultipartUploadExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowFileMultipartUploadPort
{
    ValueTask<WorkflowFileMultipartUploadResult> UploadAsync(
        WorkflowFileMultipartUploadRequest request,
        CancellationToken cancellationToken = default);
}
