using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Inspects actor state via committed projections (readmodel).
/// Never reads actor internals directly.
/// </summary>
public sealed class ActorInspectTool : IAgentTool
{
    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly WorkflowToolOptions _options;

    public ActorInspectTool(
        IWorkflowExecutionQueryApplicationService queryService,
        WorkflowToolOptions options)
    {
        _queryService = queryService;
        _options = options;
    }

    public string Name => "actor_inspect";

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    public string Description =>
        "Inspect actor state via the projection readmodel. " +
        "Shows actor snapshots (status, output, step counts), " +
        "and registered agents. " +
        "All data is from committed projections, not live actor internals.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["snapshot", "list", "agents"],
              "description": "Action: 'snapshot' (default) actor state, 'list' all actors, 'agents' registered agents"
            },
            "actor_id": {
              "type": "string",
              "description": "Actor ID (required for 'snapshot')"
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
        if (!_queryService.ActorQueryEnabled)
            return """{"error":"Actor query endpoints are not enabled on this deployment."}""";

        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            var action = args.Str("action", "snapshot");

            return action switch
            {
                "list" or "agents" => await ListAgentsAsync(ct),
                "snapshot" => await GetSnapshotAsync(args, ct),
                _ => JsonSerializer.Serialize(new { error = $"Unsupported actor_inspect action '{action}'" }),
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
        var actorId = args.Str("actor_id");
        if (string.IsNullOrWhiteSpace(actorId))
            return """{"error":"'actor_id' is required. Use action='list' to find actors."}""";

        var snapshot = await _queryService.GetActorSnapshotAsync(actorId, ct);
        if (snapshot == null)
            return JsonSerializer.Serialize(new { error = $"No snapshot found for actor '{actorId}'" });

        return JsonSerializer.Serialize(new
        {
            actor_id = snapshot.ActorId, workflow_name = snapshot.WorkflowName,
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
