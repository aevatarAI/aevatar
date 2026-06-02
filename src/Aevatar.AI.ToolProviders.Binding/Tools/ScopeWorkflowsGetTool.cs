using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

/// <summary>
/// Get a workflow summary from the current scope.
/// Delegates to IScopeWorkflowQueryPort.GetByWorkflowIdAsync.
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

            var scopeId = AgentToolRequestContext.ScopeId;
            if (string.IsNullOrWhiteSpace(scopeId))
                return JsonDefaults.Error("scope_id not available in request context");

            // Refactor (iter97/cluster-598): Old/New
            //   Old pattern: LLM callers had no workflow-id specific adapter over scope workflow queries.
            //   New principle: keep get semantics on the typed query port and return an honest not-found result.
            var workflow = await _queryPort.GetByWorkflowIdAsync(scopeId.Trim(), workflowId.Trim(), ct);
            if (workflow is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    scope_id = scopeId.Trim(),
                    workflow_id = workflowId.Trim(),
                    error = $"Workflow '{workflowId.Trim()}' was not found in scope '{scopeId.Trim()}'.",
                }, JsonDefaults.SnakeCase);
            }

            return JsonSerializer.Serialize(new
            {
                available = true,
                scope_id = scopeId.Trim(),
                workflow,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Workflow get failed: {ex.GetType().Name}");
        }
    }
}
