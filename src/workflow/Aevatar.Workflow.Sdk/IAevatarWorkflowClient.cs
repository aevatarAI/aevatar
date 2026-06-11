using System.Text.Json;
using Aevatar.Workflow.Sdk.Contracts;

namespace Aevatar.Workflow.Sdk;

public interface IAevatarWorkflowClient
{
    IAsyncEnumerable<WorkflowEvent> StartRunStreamAsync(
        ChatRunRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunResult> RunToCompletionAsync(
        ChatRunRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkflowResumeResponse> ResumeAsync(
        WorkflowResumeRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkflowSignalResponse> SignalAsync(
        WorkflowSignalRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JsonElement>> GetWorkflowCatalogAsync(
        CancellationToken cancellationToken = default);

    Task<JsonElement?> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    Task<JsonElement?> GetWorkflowDetailAsync(
        string workflowName,
        CancellationToken cancellationToken = default);

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: SDK exposed actor snapshot lookup by actorId.
    //   New principle: SDK exposes workflow actor current-state lookup by actorId.
    Task<JsonElement?> GetWorkflowActorCurrentStateAsync(
        string actorId,
        CancellationToken cancellationToken = default);

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.
    Task<IReadOnlyList<JsonElement>> GetWorkflowRunTimelineExportAsync(
        string workflowRunId,
        int take = 200,
        CancellationToken cancellationToken = default);
}
