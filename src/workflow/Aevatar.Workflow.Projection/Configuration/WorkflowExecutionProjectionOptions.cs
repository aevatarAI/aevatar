using Aevatar.CQRS.Projection.Core.Abstractions;

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
    /// Exposes workflow actor current-state readmodel query endpoints.
    /// </summary>
    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: current-state query enablement was named EnableActorQueryEndpoints.
    //   New principle: current-state query enablement is named for workflow-run readmodel semantics.
    public bool WorkflowActorCurrentStateQueryEnabled { get; set; } = true;

    /// <summary>
    /// Exposes workflow artifact/export query endpoints.
    /// </summary>
    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    public bool WorkflowArtifactQueryEnabled { get; set; } = true;

    bool IProjectionRuntimeOptions.EnableRunQueryEndpoints => WorkflowArtifactQueryEnabled;

    /// <summary>
    /// Writes run report documents/export outputs (json/html).
    /// </summary>
    public bool EnableRunReportDocuments { get; set; } = true;

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
    /// Enables background reconciliation of stale running workflow read models.
    /// </summary>
    public bool EnableTerminalStateReconciliation { get; set; } = true;

    /// <summary>
    /// Minimum read-model inactivity age before a running workflow is offered
    /// to its authoritative actor for terminal-state reconciliation.
    /// </summary>
    public int TerminalStateReconciliationStaleAfterSeconds { get; set; } = 900;

}
