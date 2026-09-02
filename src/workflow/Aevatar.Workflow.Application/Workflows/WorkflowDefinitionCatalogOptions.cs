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
    /// Pre-loads the channel-less <c>studio</c> workflow used by the <c>/workflow/studio</c> authoring surface
    /// (workflow-first + Observatory-delivered steering). Selected per-request via <c>workflow: "studio"</c>;
    /// other <c>Direct</c> callers are unaffected.
    /// </summary>
    public bool RegisterBuiltInStudioWorkflow { get; set; } = true;
}
