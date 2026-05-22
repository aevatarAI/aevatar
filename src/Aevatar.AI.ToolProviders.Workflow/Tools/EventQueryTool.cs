using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Queries committed events for a workflow run via the projection timeline export.
/// All data comes from committed projection readmodels, not the event store directly.
/// </summary>
public sealed class EventQueryTool : IAgentTool
{
    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly WorkflowToolOptions _options;

    public EventQueryTool(
        IWorkflowExecutionQueryApplicationService queryService,
        WorkflowToolOptions options)
    {
        _queryService = queryService;
        _options = options;
    }

    public string Name => "event_query";

    public string Description =>
        "Query committed events for a workflow run. " +
        "Shows the chronological timeline of execution: " +
        "step requests, completions, role replies, errors, and state transitions. " +
        "Optionally filter by stage or event type. " +
        "Use 'edges' action to see workflow run graph export edges.";

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: event_query required actor_id for workflow history and graph lookups.
    //   New principle: workflow_run_id is the artifact/export identity, while actor_id is a deprecated compatibility alias.
    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["timeline", "edges"],
              "description": "Action: 'timeline' (default) chronological events, 'edges' actor communication graph"
            },
            "workflow_run_id": {
              "type": "string",
              "description": "Workflow run ID to query"
            },
            "actor_id": {
              "type": "string",
              "description": "Deprecated alias for workflow_run_id"
            },
            "stage_filter": {
              "type": "string",
              "description": "Filter by stage (e.g. 'StepRequested', 'StepCompleted', 'RoleReply', 'Error')"
            },
            "event_type_filter": {
              "type": "string",
              "description": "Filter by event type URL pattern"
            },
            "edge_types": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Filter edges by type (for 'edges' action)"
            },
            "take": {
              "type": "integer",
              "description": "Max events to return (default: 50, max: 200)"
            }
          },
          "anyOf": [
            { "required": ["workflow_run_id"] },
            { "required": ["actor_id"] }
          ]
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
            var workflowRunId = GetWorkflowRunId(args);
            if (string.IsNullOrWhiteSpace(workflowRunId))
                return """{"error":"'workflow_run_id' is required"}""";

            var action = args.Str("action", "timeline");
            return action switch
            {
                "edges" => await GetEdgesAsync(workflowRunId, args, ct),
                _ => await GetTimelineAsync(workflowRunId, args, ct),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: event_query exposed workflow timeline as an actor timeline readmodel query keyed by actor_id.
    //   New principle: timeline data is a workflow-run timeline export artifact; actor_id remains only as a deprecated caller alias.
    private async Task<string> GetTimelineAsync(string workflowRunId, ToolArgs args, CancellationToken ct)
    {
        var take = Math.Clamp(args.Int("take") ?? _options.MaxTimelineItems, 1, 200);
        var stageFilter = args.Str("stage_filter");
        var eventTypeFilter = args.Str("event_type_filter");

        var timeline = await _queryService.ListWorkflowRunTimelineExportAsync(workflowRunId, take, ct);
        IEnumerable<WorkflowRunTimelineExportItem> filtered = timeline;

        if (!string.IsNullOrWhiteSpace(stageFilter))
            filtered = filtered.Where(t => t.Stage.Contains(stageFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(eventTypeFilter))
            filtered = filtered.Where(t => !string.IsNullOrWhiteSpace(t.EventType) &&
                t.EventType.Contains(eventTypeFilter, StringComparison.OrdinalIgnoreCase));

        var events = filtered.Select(t => new
        {
            t.Timestamp, t.Stage, message = Truncate(t.Message, 300),
            agent_id = NullIfEmpty(t.AgentId), step_id = NullIfEmpty(t.StepId),
            step_type = NullIfEmpty(t.StepType), event_type = NullIfEmpty(t.EventType),
            data = t.Data.Count > 0 ? TruncateData(t.Data) : null,
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            workflow_run_id = workflowRunId, events, count = events.Length, total_available = timeline.Count,
        }, s_json);
    }

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: event_query exposed graph edges as an actor graph readmodel query keyed by actor_id.
    //   New principle: graph edges are workflow-run graph export artifact data; actor_id remains only as a deprecated caller alias.
    private async Task<string> GetEdgesAsync(string workflowRunId, ToolArgs args, CancellationToken ct)
    {
        var take = Math.Clamp(args.Int("take") ?? 200, 1, 500);
        var edgeTypes = args.StrArray("edge_types");

        var options = edgeTypes.Length > 0
            ? new WorkflowRunGraphExportQueryOptions { EdgeTypes = edgeTypes }
            : null;

        var edges = await _queryService.ListWorkflowRunGraphExportEdgesAsync(workflowRunId, take, options, ct);

        return JsonSerializer.Serialize(new
        {
            workflow_run_id = workflowRunId,
            edges = edges.Select(e => new
            {
                id = e.EdgeId, from = e.FromNodeId, to = e.ToNodeId,
                type = e.EdgeType, updated_at = e.UpdatedAt,
                properties = e.Properties.Count > 0 ? e.Properties : null,
            }).ToArray(),
            count = edges.Count,
        }, s_json);
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max] + "...";

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow tool calls treated actor_id as the artifact query identity.
    //   New principle: workflow_run_id is the artifact identity; actor_id is accepted only for transitional tool compatibility.
    private static string GetWorkflowRunId(ToolArgs args) =>
        args.Str("workflow_run_id") ?? args.Str("actor_id") ?? string.Empty;

    private static Dictionary<string, string> TruncateData(IDictionary<string, string> data) =>
        data.ToDictionary(kv => kv.Key, kv => kv.Value.Length > 200 ? kv.Value[..200] + "..." : kv.Value);
}
