namespace Aevatar.Workflow.Application.Workflows;

/// <summary>
/// Options controlling which built-in workflow definitions are pre-loaded
/// into the <see cref="WorkflowDefinitionCatalog"/> at startup.
/// </summary>
public sealed class WorkflowDefinitionCatalogOptions
{
    public bool RegisterBuiltInDirectWorkflow { get; set; } = true;
    public bool RegisterBuiltInAutoWorkflow { get; set; } = true;
    public bool RegisterBuiltInAutoReviewWorkflow { get; set; } = true;
}
