namespace Aevatar.Workflow.Application.Runs;

/// <summary>
/// Runtime behavior options for workflow run selection and fallback handling.
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

    /// <summary>
    /// Enables fallback to the direct workflow when the request and exception match the policy.
    /// </summary>
    public bool EnableDirectFallback { get; set; } = true;

    /// <summary>
    /// Workflow names that are allowed to trigger direct fallback.
    /// </summary>
    public ISet<string> DirectFallbackWorkflowWhitelist { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AutoWorkflowName,
            AutoReviewWorkflowName,
        };

    /// <summary>
    /// Exception types that are allowed to trigger direct fallback.
    /// </summary>
    public ISet<Type> DirectFallbackExceptionWhitelist { get; } =
        new HashSet<Type>
        {
            typeof(WorkflowDirectFallbackTriggerException),
        };
}
