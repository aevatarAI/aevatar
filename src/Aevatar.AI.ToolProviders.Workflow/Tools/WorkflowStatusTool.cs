using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Queries the status and execution report of a workflow run.
/// All data comes from committed projections (readmodel), not actor internals.
/// </summary>
public sealed class WorkflowStatusTool : IAgentTool
{
    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly WorkflowToolOptions _options;

    public WorkflowStatusTool(
        IWorkflowExecutionQueryApplicationService queryService,
        WorkflowToolOptions options)
    {
        _queryService = queryService;
        _options = options;
    }

    public string Name => "workflow_status";

    public string Description =>
        "Query the status of a workflow execution. " +
        "Shows completion status, steps, role replies, and timeline events. " +
        "Use 'list' action to see available workflows, 'catalog' for definitions, " +
        "or provide an actor_id to get a specific run's status.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["status", "list", "catalog", "detail", "timeline"],
              "description": "Action: 'status' (default) run report, 'list' available workflows, 'catalog' definitions, 'detail' specific definition, 'timeline' execution timeline"
            },
            "actor_id": {
              "type": "string",
              "description": "Workflow actor ID (required for 'status' and 'timeline')"
            },
            "workflow_name": {
              "type": "string",
              "description": "Workflow name for 'detail' action"
            },
            "take": {
              "type": "integer",
              "description": "Max items to return (default: 50)"
            }
          }
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
            var action = args.Str("action", "status");

            return action switch
            {
                "list" => ListWorkflows(),
                "catalog" => ListCatalog(),
                "detail" => GetDetail(args),
                "timeline" => await GetTimelineAsync(args, ct),
                _ => await GetStatusAsync(args, ct),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private string ListWorkflows()
    {
        var workflows = _queryService.ListWorkflows();
        return JsonSerializer.Serialize(new { workflows, count = workflows.Count }, s_json);
    }

    private string ListCatalog()
    {
        var catalog = _queryService.ListWorkflowCatalog();
        var items = catalog.Select(c => new
        {
            name = c.Name, description = c.Description, category = c.Category,
            group = c.Group, source = c.Source, requires_llm = c.RequiresLlmProvider,
        }).ToArray();
        return JsonSerializer.Serialize(new { workflows = items, count = items.Length }, s_json);
    }

    private string GetDetail(ToolArgs args)
    {
        var name = args.Str("workflow_name");
        if (string.IsNullOrWhiteSpace(name))
            return """{"error":"'workflow_name' is required for 'detail' action"}""";

        var detail = _queryService.GetWorkflowDetail(name);
        if (detail == null)
            return JsonSerializer.Serialize(new { error = $"Workflow '{name}' not found" });

        return JsonSerializer.Serialize(new
        {
            name = detail.Catalog.Name, description = detail.Catalog.Description,
            roles = detail.Definition.Roles.Select(r => new { id = r.Id, name = r.Name, provider = r.Provider, model = r.Model }).ToArray(),
            steps = detail.Definition.Steps.Select(s => new { id = s.Id, type = s.Type, target_role = s.TargetRole, next = s.Next }).ToArray(),
            edges = detail.Edges.Select(e => new { from = e.From, to = e.To, label = e.Label }).ToArray(),
        }, s_json);
    }

    private async Task<string> GetStatusAsync(ToolArgs args, CancellationToken ct)
    {
        var actorId = args.Str("actor_id");
        if (string.IsNullOrWhiteSpace(actorId))
            return """{"error":"'actor_id' is required for 'status' action. Use action='list' to find available workflows."}""";

        var report = await _queryService.GetActorReportAsync(actorId, ct);
        if (report == null)
            return JsonSerializer.Serialize(new { error = $"No workflow run found for actor '{actorId}'" });

        return JsonSerializer.Serialize(new
        {
            actor_id = report.RootActorId, workflow_name = report.WorkflowName,
            status = report.CompletionStatus.ToString(), state_version = report.StateVersion,
            started_at = report.StartedAt, ended_at = report.EndedAt,
            duration_ms = report.DurationMs, success = report.Success,
            input = Truncate(report.Input, 500), final_output = Truncate(report.FinalOutput, 1000),
            final_error = string.IsNullOrWhiteSpace(report.FinalError) ? null : report.FinalError,
            summary = new
            {
                report.Summary.TotalSteps, report.Summary.RequestedSteps,
                report.Summary.CompletedSteps, report.Summary.RoleReplyCount,
                report.Summary.StepTypeCounts,
            },
            steps = report.Steps.Select(s => new
            {
                s.StepId, s.StepType, s.TargetRole, s.Success, s.DurationMs,
                output_preview = Truncate(s.OutputPreview, 200),
                error = string.IsNullOrWhiteSpace(s.Error) ? null : s.Error,
            }).ToArray(),
            topology = report.Topology.Select(t => new { t.Parent, t.Child }).ToArray(),
        }, s_json);
    }

    private async Task<string> GetTimelineAsync(ToolArgs args, CancellationToken ct)
    {
        var actorId = args.Str("actor_id");
        if (string.IsNullOrWhiteSpace(actorId))
            return """{"error":"'actor_id' is required for 'timeline' action"}""";

        var take = Math.Clamp(args.Int("take") ?? _options.MaxTimelineItems, 1, 200);
        var timeline = await _queryService.ListActorTimelineAsync(actorId, take, ct);

        return JsonSerializer.Serialize(new
        {
            actor_id = actorId,
            events = timeline.Select(t => new
            {
                t.Timestamp, t.Stage, t.Message,
                step_id = string.IsNullOrWhiteSpace(t.StepId) ? null : t.StepId,
                step_type = string.IsNullOrWhiteSpace(t.StepType) ? null : t.StepType,
                event_type = string.IsNullOrWhiteSpace(t.EventType) ? null : t.EventType,
            }).ToArray(),
            count = timeline.Count,
        }, s_json);
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max] + "...";
}
