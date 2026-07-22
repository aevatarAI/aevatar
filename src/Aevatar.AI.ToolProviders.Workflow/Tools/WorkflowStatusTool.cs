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

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    public string Description =>
        "Query the status of a workflow execution. " +
        "Shows completion status, steps, role replies, and timeline events. " +
        "Use 'list' action to see available workflows, or provide a workflow_run_id " +
        "to get a specific run's status.";

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["status", "list", "timeline"],
              "description": "Action: 'status' (default) run report, 'list' available workflows, 'timeline' execution timeline"
            },
            "workflow_run_id": {
              "type": "string",
              "description": "Workflow run ID (required for 'status' and 'timeline')"
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
                "status" => await GetStatusAsync(args, ct),
                "list" => ListWorkflows(),
                "timeline" => await GetTimelineAsync(args, ct),
                _ => JsonSerializer.Serialize(new { error = $"Unsupported action '{action}'" }),
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

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    private async Task<string> GetStatusAsync(ToolArgs args, CancellationToken ct)
    {
        var workflowRunId = args.Str("workflow_run_id");
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return """{"error":"'workflow_run_id' is required for 'status' action. Use action='list' to find available workflows."}""";

        var report = await _queryService.GetWorkflowRunReportArtifactAsync(workflowRunId, ct);
        if (report == null)
            return JsonSerializer.Serialize(new { error = $"No workflow run found for '{workflowRunId}'" });

        return JsonSerializer.Serialize(new
        {
            workflow_run_id = report.RootActorId, workflow_name = report.WorkflowName,
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

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    private async Task<string> GetTimelineAsync(ToolArgs args, CancellationToken ct)
    {
        var workflowRunId = args.Str("workflow_run_id");
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return """{"error":"'workflow_run_id' is required for 'timeline' action"}""";

        var take = Math.Clamp(args.Int("take") ?? _options.MaxTimelineItems, 1, 200);
        var timeline = await _queryService.ListWorkflowRunTimelineExportAsync(workflowRunId, take, ct);

        return JsonSerializer.Serialize(new
        {
            workflow_run_id = workflowRunId,
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
