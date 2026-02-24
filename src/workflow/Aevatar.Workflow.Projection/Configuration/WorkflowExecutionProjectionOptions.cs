namespace Aevatar.Workflow.Projection.Configuration;

/// <summary>
/// Feature flags for chat projection pipeline.
/// </summary>
public sealed class WorkflowExecutionProjectionOptions
    : IProjectionRuntimeOptions
{

    /// <summary>
    /// Enables projection pipeline registration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Exposes read-side actor query endpoints.
    /// </summary>
    public bool EnableActorQueryEndpoints { get; set; } = true;

    public bool EnableRunQueryEndpoints
    {
        get => EnableActorQueryEndpoints;
        set => EnableActorQueryEndpoints = value;
    }

    /// <summary>
    /// Writes run report artifacts (json/html).
    /// </summary>
    public bool EnableRunReportArtifacts { get; set; } = true;

    /// <summary>
    /// Max wait time for one run projection completion signal.
    /// </summary>
    public int RunProjectionCompletionWaitTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Extra grace wait before force-finalize when completion status is timeout.
    /// </summary>
    public int RunProjectionFinalizeGraceTimeoutMs { get; set; } = 1500;

    /// <summary>
    /// Whether to pre-validate read-model provider selection and capabilities during host startup.
    /// </summary>
    public bool ValidateReadModelProviderOnStartup { get; set; } = true;

    /// <summary>
    /// Whether to pre-validate relation provider selection and capabilities during host startup.
    /// </summary>
    public bool ValidateRelationProviderOnStartup { get; set; } = true;
}
