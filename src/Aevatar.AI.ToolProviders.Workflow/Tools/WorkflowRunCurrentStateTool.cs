using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Reads workflow-run current state via committed projections (readmodel).
/// Never reads actor internals directly.
/// </summary>
public sealed class WorkflowRunCurrentStateTool : IAgentTool
{
    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly WorkflowToolOptions _options;

    public WorkflowRunCurrentStateTool(
        IWorkflowExecutionQueryApplicationService queryService,
        WorkflowToolOptions options)
    {
        _queryService = queryService;
        _options = options;
    }

    public string Name => "workflow_run_current_state";

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: workflow tooling exposed actor_inspect with actor_id snapshot semantics.
    //   New principle: workflow tooling exposes workflow-run current-state readmodel semantics by workflow_run_id.
    public string Description =>
        "Read workflow-run current state via the projection readmodel. " +
        "Shows workflow-run status, output, step counts, and registered agents. " +
        "All data is from committed projections, not live actor internals.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["snapshot", "list", "agents"],
              "description": "Action: 'snapshot' (default) workflow-run current state, 'list' all workflow runs, 'agents' registered agents"
            },
            "workflow_run_id": {
              "type": "string",
              "description": "Workflow run ID (required for 'snapshot')"
            },
            "take": {
              "type": "integer",
              "description": "Max items to return (default: 100)"
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
        if (!_queryService.WorkflowRunCurrentStateQueryEnabled)
            return """{"error":"Workflow-run current-state query is not enabled on this deployment."}""";

        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            var action = args.Str("action", "snapshot");

            return action switch
            {
                "list" or "agents" => await ListAgentsAsync(ct),
                "snapshot" => await GetSnapshotAsync(args, ct),
                _ => JsonSerializer.Serialize(new { error = $"Unsupported workflow_run_current_state action '{action}'" }),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private async Task<string> GetSnapshotAsync(ToolArgs args, CancellationToken ct)
    {
        var workflowRunId = args.Str("workflow_run_id");
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return """{"error":"'workflow_run_id' is required. Use action='list' to find workflow runs."}""";

        var snapshot = await _queryService.GetWorkflowRunCurrentStateAsync(workflowRunId, ct);
        if (snapshot == null)
            return JsonSerializer.Serialize(new { error = $"No current state found for workflow run '{workflowRunId}'" });

        return JsonSerializer.Serialize(new
        {
            workflow_run_id = snapshot.ActorId, workflow_name = snapshot.WorkflowName,
            status = snapshot.CompletionStatus.ToString(), state_version = snapshot.StateVersion,
            last_command_id = snapshot.LastCommandId, last_event_id = snapshot.LastEventId,
            last_updated_at = snapshot.LastUpdatedAt, last_success = snapshot.LastSuccess,
            last_output = Truncate(snapshot.LastOutput, 500),
            last_error = string.IsNullOrWhiteSpace(snapshot.LastError) ? null : snapshot.LastError,
            steps = new
            {
                total = snapshot.TotalSteps, requested = snapshot.RequestedSteps,
                completed = snapshot.CompletedSteps, role_replies = snapshot.RoleReplyCount,
            },
        }, s_json);
    }

    private async Task<string> ListAgentsAsync(CancellationToken ct)
    {
        var agents = await _queryService.ListAgentsAsync(ct);
        return JsonSerializer.Serialize(new
        {
            agents = agents.Select(a => new { a.Id, a.Type, a.Description }).ToArray(),
            count = agents.Count,
        }, s_json);
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max] + "...";
}
