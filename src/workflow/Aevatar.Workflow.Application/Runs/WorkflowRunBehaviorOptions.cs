namespace Aevatar.Workflow.Application.Runs;

/// <summary>
/// Runtime behavior options for workflow run selection.
/// </summary>
public sealed class WorkflowRunBehaviorOptions
{
    public const string DirectWorkflowName = "direct";
    public const string AutoWorkflowName = "auto";
    public const string AutoReviewWorkflowName = "auto_review";

    /// <summary>
    /// Default workflow name when no explicit workflow is requested.
    /// </summary>
    public string DefaultWorkflowName { get; set; } = DirectWorkflowName;

    /// <summary>
    /// Whether to route unspecified workflow requests to <c>auto</c> instead of <c>direct</c>.
    /// </summary>
    public bool UseAutoAsDefaultWhenWorkflowUnspecified { get; set; }

}
