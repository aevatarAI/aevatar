using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionArtifactQueryPort : IWorkflowExecutionArtifactQueryPort
{
    private readonly IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> _reportReader;
    private readonly IProjectionGraphStore _graphStore;
    private readonly WorkflowExecutionReadModelMapper _mapper;
    private readonly bool _enableActorQueryEndpoints;

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.
    public WorkflowExecutionArtifactQueryPort(
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> reportReader,
        WorkflowExecutionReadModelMapper mapper,
        IProjectionGraphStore graphStore,
        WorkflowExecutionProjectionOptions? options = null)
    {
        _reportReader = reportReader ?? throw new ArgumentNullException(nameof(reportReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _enableActorQueryEndpoints = options == null || (options.Enabled && options.EnableActorQueryEndpoints);
    }

    public bool EnableActorQueryEndpoints => _enableActorQueryEndpoints;

    public async Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(
        string workflowRunId,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints || string.IsNullOrWhiteSpace(workflowRunId))
            return null;

        var report = await _reportReader.GetAsync(workflowRunId, ct);
        return report == null ? null : _mapper.ToRunReport(report);
    }

    public async Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
        string workflowRunId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints || string.IsNullOrWhiteSpace(workflowRunId))
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var report = await _reportReader.GetAsync(workflowRunId, ct);
        if (report == null)
            return [];

        return report.Timeline
            .OrderByDescending(x => x.Timestamp)
            .Take(boundedTake)
            .Select(_mapper.ToWorkflowRunTimelineExportItem)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkflowRunGraphExportEdge>> GetWorkflowRunGraphExportEdgesAsync(
        string workflowRunId,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints)
            return [];

        var workflowRunIdValue = workflowRunId?.Trim() ?? "";
        if (workflowRunIdValue.Length == 0)
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var direction = MapDirection(options?.Direction ?? WorkflowRunGraphExportDirection.Both);
        var edgeTypes = NormalizeEdgeTypes(options?.EdgeTypes);
        var edges = await _graphStore.GetNeighborsAsync(
            new ProjectionGraphQuery
            {
                Scope = WorkflowExecutionGraphConstants.Scope,
                RootNodeId = workflowRunIdValue,
                Direction = direction,
                EdgeTypes = edgeTypes,
                Take = boundedTake,
            },
            ct);
        return edges.Select(_mapper.ToWorkflowRunGraphExportEdge).ToList();
    }

    public async Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
        string workflowRunId,
        int depth = 2,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints)
            return new WorkflowRunGraphExportSubgraph
            {
                RootNodeId = workflowRunId ?? string.Empty,
            };

        var workflowRunIdValue = workflowRunId?.Trim() ?? "";
        if (workflowRunIdValue.Length == 0)
            return new WorkflowRunGraphExportSubgraph();

        var boundedDepth = Math.Clamp(depth, 1, 8);
        var boundedTake = Math.Clamp(take, 1, 2000);
        var direction = MapDirection(options?.Direction ?? WorkflowRunGraphExportDirection.Both);
        var edgeTypes = NormalizeEdgeTypes(options?.EdgeTypes);
        var subgraph = await _graphStore.GetSubgraphAsync(
            new ProjectionGraphQuery
            {
                Scope = WorkflowExecutionGraphConstants.Scope,
                RootNodeId = workflowRunIdValue,
                Direction = direction,
                EdgeTypes = edgeTypes,
                Depth = boundedDepth,
                Take = boundedTake,
            },
            ct);
        return _mapper.ToWorkflowRunGraphExportSubgraph(workflowRunIdValue, subgraph);
    }

    private static ProjectionGraphDirection MapDirection(WorkflowRunGraphExportDirection direction)
    {
        return direction switch
        {
            WorkflowRunGraphExportDirection.Outbound => ProjectionGraphDirection.Outbound,
            WorkflowRunGraphExportDirection.Inbound => ProjectionGraphDirection.Inbound,
            _ => ProjectionGraphDirection.Both,
        };
    }

    private static IReadOnlyList<string> NormalizeEdgeTypes(IReadOnlyList<string>? edgeTypes)
    {
        if (edgeTypes == null || edgeTypes.Count == 0)
            return [];

        return edgeTypes
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
