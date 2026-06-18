using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Reads workflow actor current state via committed projections (readmodel).
/// Never reads actor internals directly.
/// </summary>
public sealed class WorkflowActorCurrentStateTool : IAgentTool
{
    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly WorkflowToolOptions _options;

    public WorkflowActorCurrentStateTool(
        IWorkflowExecutionQueryApplicationService queryService,
        WorkflowToolOptions options)
    {
        _queryService = queryService;
        _options = options;
    }

    public string Name => "workflow_actor_current_state";

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: workflow tooling exposed actor_inspect with actor_id snapshot semantics.
    //   New principle: workflow tooling exposes workflow actor current-state readmodel semantics by actor_id.
    public string Description =>
        "Read workflow actor current state via the projection readmodel. " +
        "Shows workflow actor status, output, step counts, and registered agents. " +
        "All data is from committed projections, not live actor internals.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["snapshot", "list", "agents", "dead_letters"],
              "description": "Action: 'snapshot' (default) workflow actor current state, 'list' all workflow actors, 'agents' registered agents, 'dead_letters' compensation dead-letter workflow actors"
            },
            "actor_id": {
              "type": "string",
              "description": "Workflow actor ID (required for 'snapshot')"
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
        if (!_queryService.WorkflowActorCurrentStateQueryEnabled)
            return """{"error":"Workflow actor current-state query is not enabled on this deployment."}""";

        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            var action = args.Str("action", "snapshot");

            return action switch
            {
                "list" or "agents" => await ListAgentsAsync(ct),
                "dead_letters" => await ListDeadLettersAsync(args, ct),
                "snapshot" => await GetSnapshotAsync(args, ct),
                _ => JsonSerializer.Serialize(new { error = $"Unsupported workflow_actor_current_state action '{action}'" }),
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
            return """{"error":"'actor_id' is required. Use action='list' to find workflow actors."}""";

        var snapshot = await _queryService.GetWorkflowActorCurrentStateAsync(actorId, ct);
        if (snapshot == null)
            return JsonSerializer.Serialize(new { error = $"No current state found for workflow actor '{actorId}'" });

        return JsonSerializer.Serialize(new
        {
            actor_id = snapshot.ActorId, workflow_name = snapshot.WorkflowName,
            status = snapshot.CompletionStatus.ToString(), state_version = snapshot.StateVersion,
            last_command_id = snapshot.LastCommandId, last_event_id = snapshot.LastEventId,
            last_updated_at = snapshot.LastUpdatedAt, last_success = snapshot.LastSuccess,
            last_output = Truncate(snapshot.LastOutput, 500),
            last_error = string.IsNullOrWhiteSpace(snapshot.LastError) ? null : snapshot.LastError,
            saga_status = snapshot.SagaStatus.ToString(),
            dead_letter = BuildDeadLetter(snapshot),
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

    private async Task<string> ListDeadLettersAsync(ToolArgs args, CancellationToken ct)
    {
        var take = Math.Clamp(args.Int("take") ?? 100, 1, 200);
        var snapshots = await _queryService.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = take,
                SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
            },
            ct);

        return JsonSerializer.Serialize(new
        {
            dead_letters = snapshots.Select(snapshot => new
            {
                actor_id = snapshot.ActorId,
                workflow_name = snapshot.WorkflowName,
                status = snapshot.CompletionStatus.ToString(),
                saga_status = snapshot.SagaStatus.ToString(),
                state_version = snapshot.StateVersion,
                last_event_id = snapshot.LastEventId,
                last_updated_at = snapshot.LastUpdatedAt,
                last_error = string.IsNullOrWhiteSpace(snapshot.LastError) ? null : snapshot.LastError,
                dead_letter = BuildDeadLetter(snapshot),
            }).ToArray(),
            count = snapshots.Count,
        }, s_json);
    }

    private static object? BuildDeadLetter(WorkflowActorSnapshot snapshot)
    {
        if (snapshot.SagaStatus != WorkflowSagaStatus.CompensationDeadLetter)
            return null;

        return new
        {
            failed_compensation_step_id = snapshot.DeadLetterFailedCompensationStepId,
            remaining_uncompensated = snapshot.DeadLetterRemainingUncompensated,
            error = string.IsNullOrWhiteSpace(snapshot.DeadLetterError) ? null : snapshot.DeadLetterError,
        };
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max] + "...";
}
