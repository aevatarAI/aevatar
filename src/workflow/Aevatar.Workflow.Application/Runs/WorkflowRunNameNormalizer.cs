namespace Aevatar.Workflow.Application.Runs;

internal static class WorkflowRunNameNormalizer
{
    public static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName) ? string.Empty : workflowName.Trim();
}
