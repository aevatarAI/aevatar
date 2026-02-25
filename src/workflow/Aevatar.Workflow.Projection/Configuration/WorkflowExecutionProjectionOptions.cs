using Aevatar.CQRS.Projection.Core.Orchestration;

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
    public bool ValidateDocumentProviderOnStartup { get; set; } = true;

    /// <summary>
    /// Whether to pre-validate graph provider selection and capabilities during host startup.
    /// </summary>
    public bool ValidateGraphProviderOnStartup { get; set; } = true;

    /// <summary>
    /// Projection ownership lease TTL in milliseconds.
    /// </summary>
    public long ProjectionOwnershipLeaseTtlMs { get; set; } =
        ProjectionOwnershipCoordinatorOptions.DefaultLeaseTtlMs;

    /// <summary>
    /// Enables durable outbox replay for projection store dispatch compensation.
    /// </summary>
    public bool EnableDispatchCompensationReplay { get; set; } = true;

    /// <summary>
    /// Poll interval for compensation replay worker.
    /// </summary>
    public int DispatchCompensationReplayPollIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Max records replayed in one compensation polling cycle.
    /// </summary>
    public int DispatchCompensationReplayBatchSize { get; set; } = 20;

    /// <summary>
    /// Base retry delay for failed compensation replay.
    /// </summary>
    public int DispatchCompensationReplayBaseDelayMs { get; set; } = 1000;

    /// <summary>
    /// Max retry delay for failed compensation replay.
    /// </summary>
    public int DispatchCompensationReplayMaxDelayMs { get; set; } = 60_000;
}
