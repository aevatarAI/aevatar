using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Workflow.Projection.Projectors;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowRunInsightReportGraphMaterializer
    : IProjectionGraphMaterializer<WorkflowRunInsightReportDocument>
{
    private static readonly WorkflowRunGraphArtifactMaterializer Inner = new();

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.
    public ProjectionGraphMaterialization Materialize(WorkflowRunInsightReportDocument readModel)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        return Inner.Materialize(readModel);
    }
}
