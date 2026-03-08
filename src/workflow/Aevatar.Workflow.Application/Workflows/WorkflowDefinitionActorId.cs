namespace Aevatar.Workflow.Application.Workflows;

/// <summary>
/// Canonical actor id strategy for registry-backed workflow definition actors.
/// </summary>
public static class WorkflowDefinitionActorId
{
    private const string Prefix = "workflow-definition:";

    public static string Format(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            throw new ArgumentException("Workflow name is required.", nameof(workflowName));

        return Prefix + workflowName.Trim().ToLowerInvariant();
    }
}
