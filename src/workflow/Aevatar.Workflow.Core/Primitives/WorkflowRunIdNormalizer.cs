namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Central run id normalization utility to keep correlation keys consistent across modules.
/// </summary>
public static class WorkflowRunIdNormalizer
{
    public static string Normalize(string? runId) =>
        string.IsNullOrWhiteSpace(runId) ? "default" : runId.Trim();

    public static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName) ? string.Empty : workflowName.Trim();
}
