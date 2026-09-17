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
    /// Maximum time from command acceptance to the first projection-backed
    /// workflow frame. This is not an overall workflow execution timeout.
    /// </summary>
    public TimeSpan AcceptedObservationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum time a persistent create waits for its scope-bound reservation
    /// read model before the workflow run may be accepted.
    /// </summary>
    public TimeSpan ChatHistoryReservationObservationTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Read-only observation interval used before acknowledging a newly reserved conversation.
    /// </summary>
    public TimeSpan ChatHistoryReservationObservationInterval { get; set; } = TimeSpan.FromMilliseconds(50);

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
