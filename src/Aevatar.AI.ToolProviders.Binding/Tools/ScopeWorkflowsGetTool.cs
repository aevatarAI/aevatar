using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

/// <summary>
/// Get a workflow summary from the current scope.
/// Delegates to IScopeWorkflowQueryPort.LookupByWorkflowIdAsync.
/// </summary>
public sealed class ScopeWorkflowsGetTool : IAgentTool
{
    private readonly IScopeWorkflowQueryPort _queryPort;

    public ScopeWorkflowsGetTool(IScopeWorkflowQueryPort queryPort)
    {
        _queryPort = queryPort;
    }

    public string Name => "scope_workflows_get";

    public string Description =>
        "Get one workflow from the current scope by workflow_id. " +
        "Returns the typed workflow summary without executing the workflow.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "workflow_id": {
              "type": "string",
              "description": "Stable workflow ID inside the current scope"
            }
          },
          "required": ["workflow_id"]
        }
        """;

    public bool IsReadOnly => true;

    private static string BuildUnavailableMessage(
        string scopeId,
        string workflowId,
        ScopeWorkflowLookupStatus status) =>
        status == ScopeWorkflowLookupStatus.NotFound
            ? $"Workflow '{workflowId}' was not found in scope '{scopeId}'."
            : $"Workflow '{workflowId}' is not ready to run in scope '{scopeId}'.";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            if (args.ParseError != null)
                return JsonDefaults.Error(args.ParseError);

            var workflowId = args.Str("workflow_id");
            if (string.IsNullOrWhiteSpace(workflowId))
                return JsonDefaults.Error("'workflow_id' is required");

            var scopeId = ToolOwnerScopeResolver.Resolve();
            if (string.IsNullOrWhiteSpace(scopeId))
                return JsonDefaults.Error(ToolOwnerScopeResolver.MissingMessage);

            var lookup = await _queryPort.LookupByWorkflowIdAsync(scopeId.Trim(), workflowId.Trim(), ct);
            if (!lookup.IsRunnable)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    scope_id = scopeId.Trim(),
                    workflow_id = workflowId.Trim(),
                    status = lookup.Status.ToString(),
                    error = BuildUnavailableMessage(scopeId.Trim(), workflowId.Trim(), lookup.Status),
                }, JsonDefaults.SnakeCase);
            }

            return JsonSerializer.Serialize(new
            {
                available = true,
                scope_id = scopeId.Trim(),
                status = lookup.Status.ToString(),
                workflow = lookup.Workflow,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Workflow get failed: {ex.GetType().Name}");
        }
    }
}
