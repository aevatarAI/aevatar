namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Port for uploading workflow YAML artifacts to external storage (e.g., chrono-storage).
/// </summary>
public interface IWorkflowStoragePort
{
    Task UploadWorkflowYamlAsync(string workflowId, string workflowName, string yaml, CancellationToken ct);
}
