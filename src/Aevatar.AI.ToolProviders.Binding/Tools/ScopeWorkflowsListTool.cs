using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

/// <summary>
/// List workflows in the current scope.
/// Delegates to IScopeWorkflowQueryPort.ListAsync.
/// </summary>
public sealed class ScopeWorkflowsListTool : IAgentTool
{
    private readonly IScopeWorkflowQueryPort _queryPort;
    private readonly BindingToolOptions _options;

    public ScopeWorkflowsListTool(IScopeWorkflowQueryPort queryPort, BindingToolOptions options)
    {
        _queryPort = queryPort;
        _options = options;
    }

    public string Name => "scope_workflows_list";

    public string Description =>
        "List workflows published in the current scope. " +
        "Returns typed workflow IDs and actor/readmodel data for Ornn skill coordination.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "max_results": {
              "type": "integer",
              "description": "Maximum workflows to return"
            }
          }
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

            var scopeId = ToolOwnerScopeResolver.Resolve();
            if (string.IsNullOrWhiteSpace(scopeId))
                return JsonDefaults.Error(ToolOwnerScopeResolver.MissingMessage);

            // Refactor (iter97/cluster-598): Old/New
            //   Old pattern: LLM callers had to use generic binding views to infer workflow state.
            //   New principle: this query tool reads the existing typed scope workflow readmodel port directly.
            var workflows = await _queryPort.ListAsync(scopeId.Trim(), ct);
            var maxResults = args.Int("max_results", _options.MaxListResults);
            maxResults = Math.Clamp(maxResults, 1, _options.MaxListResults);
            var limited = workflows.Take(maxResults).ToArray();

            return JsonSerializer.Serialize(new
            {
                scope_id = scopeId.Trim(),
                count = limited.Length,
                total = workflows.Count,
                workflows = limited,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Workflow list failed: {ex.GetType().Name}");
        }
    }
}
