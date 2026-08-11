using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Queries workflow artifact/export surfaces on the existing workflow execution facade.
/// </summary>
public sealed class WorkflowArtifactQueryTool : IAgentTool
{
    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly WorkflowToolOptions _options;

    public WorkflowArtifactQueryTool(
        IWorkflowExecutionQueryApplicationService queryService,
        WorkflowToolOptions options)
    {
        _queryService = queryService;
        _options = options;
    }

    public string Name => "workflow_artifact_query";

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    public string Description =>
        "Query workflow-run graph artifact exports. " +
        "Use action='subgraph' with workflow_run_id to return the workflow graph subgraph artifact.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["subgraph"],
              "description": "Action: 'subgraph' returns the workflow-run graph subgraph artifact"
            },
            "workflow_run_id": {
              "type": "string",
              "description": "Workflow run ID to query"
            },
            "graph_depth": {
              "type": "integer",
              "description": "Graph traversal depth (default: 2, max: 5)"
            },
            "take": {
              "type": "integer",
              "description": "Max graph items to return (default: 200, max: 500)"
            }
          },
          "required": ["workflow_run_id"]
        }
        """;

    public bool IsReadOnly => true;

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            var action = args.Str("action", "subgraph");
            if (!string.Equals(action, "subgraph", StringComparison.Ordinal))
                return """{"error":"workflow_artifact_query only supports action='subgraph'"}""";

            var workflowRunId = args.Str("workflow_run_id");
            if (string.IsNullOrWhiteSpace(workflowRunId))
                return """{"error":"'workflow_run_id' is required"}""";

            return await GetSubgraphAsync(workflowRunId, args, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    private async Task<string> GetSubgraphAsync(string workflowRunId, ToolArgs args, CancellationToken ct)
    {
        var depth = Math.Clamp(args.Int("graph_depth") ?? _options.MaxGraphDepth, 1, 5);
        var take = Math.Clamp(args.Int("take") ?? 200, 1, 500);
        var subgraph = await _queryService.GetWorkflowRunGraphExportSubgraphAsync(workflowRunId, depth, take, ct: ct);

        return JsonSerializer.Serialize(new
        {
            workflow_run_id = workflowRunId,
            artifact = "graph_subgraph",
            subgraph = new
            {
                root = subgraph.RootNodeId,
                source_state_version = subgraph.SourceStateVersion,
                nodes = subgraph.Nodes.Select(n => new
                {
                    id = n.NodeId, type = n.NodeType, updated_at = n.UpdatedAt,
                    properties = n.Properties.Count > 0 ? n.Properties : null,
                }).ToArray(),
                edges = subgraph.Edges.Select(e => new
                {
                    from = e.FromNodeId, to = e.ToNodeId, type = e.EdgeType,
                    properties = e.Properties.Count > 0 ? e.Properties : null,
                }).ToArray(),
                node_count = subgraph.Nodes.Count,
                edge_count = subgraph.Edges.Count,
            },
        }, s_json);
    }
}
