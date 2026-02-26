namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Central run id normalization utility to keep correlation keys consistent across modules.
/// </summary>
public static class WorkflowRunIdNormalizer
{
    public static string Normalize(string? runId) =>
        string.IsNullOrWhiteSpace(runId) ? "default" : runId.Trim();
}
