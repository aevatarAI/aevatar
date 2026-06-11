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
    private readonly bool _workflowArtifactQueryEnabled;

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    public WorkflowExecutionArtifactQueryPort(
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> reportReader,
        WorkflowExecutionReadModelMapper mapper,
        IProjectionGraphStore graphStore,
        WorkflowExecutionProjectionOptions? options = null)
    {
        _reportReader = reportReader ?? throw new ArgumentNullException(nameof(reportReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _workflowArtifactQueryEnabled = options == null || (options.Enabled && options.WorkflowArtifactQueryEnabled);
    }

    public bool WorkflowArtifactQueryEnabled => _workflowArtifactQueryEnabled;

    public async Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(
        string workflowRunId,
        CancellationToken ct = default)
    {
        if (!_workflowArtifactQueryEnabled || string.IsNullOrWhiteSpace(workflowRunId))
            return null;

        var report = await _reportReader.GetAsync(workflowRunId, ct);
        return report == null ? null : _mapper.ToRunReport(report);
    }

    public async Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
        string workflowRunId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_workflowArtifactQueryEnabled || string.IsNullOrWhiteSpace(workflowRunId))
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
        if (!_workflowArtifactQueryEnabled)
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
        if (!_workflowArtifactQueryEnabled)
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
