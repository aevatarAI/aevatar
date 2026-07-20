using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Mainnet.Host.Api.Hosting;

internal sealed class MainnetWorkflowFileMultipartUploadSafetyPolicyResolver
    : IWorkflowFileMultipartUploadPolicyResolver
{
    public const long MaxFileBytes = 30L * 1024L * 1024L;
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "POST",
        "PUT",
        "PATCH",
    };

    public ValueTask<WorkflowFileMultipartUploadPolicyResolution> ResolveAsync(
        WorkflowFileMultipartUploadCandidate candidate,
        FileArtifactRef descriptor,
        WorkflowFileMultipartUploadExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = descriptor;
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();

        var method = NormalizeMethod(candidate.Method);
        if (method == null || !AllowedMethods.Contains(method))
        {
            return ValueTask.FromResult(WorkflowFileMultipartUploadPolicyResolution.Denied(
                "unsupported_method",
                "workflow_file_submit method is not allowed by the Mainnet multipart upload safety policy."));
        }

        if (candidate.MaxFileBytes is <= 0)
        {
            return ValueTask.FromResult(WorkflowFileMultipartUploadPolicyResolution.Denied(
                "file_too_large",
                "workflow_file_submit requested max_file_bytes must be greater than zero."));
        }

        var maxFileBytes = candidate.MaxFileBytes.HasValue
            ? Math.Min(candidate.MaxFileBytes.Value, MaxFileBytes)
            : MaxFileBytes;

        return ValueTask.FromResult(WorkflowFileMultipartUploadPolicyResolution.Allowed(
                new WorkflowFileMultipartUploadPolicy(
                candidate.ServiceSlug,
                candidate.Path,
                method,
                candidate.FileFieldName,
                new Dictionary<string, string>(candidate.FormFields, StringComparer.Ordinal),
                candidate.OutputKind ?? string.Empty,
                candidate.OutputSelector ?? string.Empty,
                maxFileBytes)));
    }

    private static string? NormalizeMethod(string? method) =>
        string.IsNullOrWhiteSpace(method) ? null : method.Trim().ToUpperInvariant();
}
