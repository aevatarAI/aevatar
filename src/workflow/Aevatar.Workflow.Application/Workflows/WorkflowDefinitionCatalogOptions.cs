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

    /// <summary>
    /// Pre-loads the frozen <c>studio</c> workflow used only by the external
    /// <c>workflow: "studio"</c> compatibility adapter. Aevatar-owned Studio chat uses the
    /// actor-owned Assistant trunk.
    /// </summary>
    public bool RegisterBuiltInStudioWorkflow { get; set; } = true;
}
