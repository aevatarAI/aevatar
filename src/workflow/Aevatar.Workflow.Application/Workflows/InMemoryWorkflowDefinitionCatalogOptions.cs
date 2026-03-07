namespace Aevatar.Workflow.Application.Workflows;

public sealed class InMemoryWorkflowDefinitionCatalogOptions
{
    public bool RegisterBuiltInDirectWorkflow { get; set; } = true;
    public bool RegisterBuiltInAutoWorkflow { get; set; } = true;
    public bool RegisterBuiltInAutoReviewWorkflow { get; set; } = true;
}
