using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Ports;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Lists registered workflow definitions with names, step counts, and revision IDs.
/// All data comes from the definition command adapter (readmodel).
/// </summary>
public sealed class WorkflowListDefsTool : IAgentTool
{
    private readonly IWorkflowDefinitionCommandAdapter _definitionCommand;

    public WorkflowListDefsTool(IWorkflowDefinitionCommandAdapter definitionCommand)
    {
        _definitionCommand = definitionCommand;
    }

    public string Name => "workflow_list_defs";

    public string Description =>
        "List registered workflow definitions with names, step counts, and revision IDs.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "filter": {
              "type": "string",
              "description": "Optional text filter for workflow names"
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
            var filter = args.Str("filter");

            var defs = await _definitionCommand.ListDefinitionsAsync(ct);

            if (!string.IsNullOrWhiteSpace(filter))
            {
                defs = defs
                    .Where(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return JsonSerializer.Serialize(new
            {
                definitions = defs.Select(d => new
                {
                    name = d.Name,
                    description = d.Description,
                    step_count = d.StepCount,
                    role_count = d.RoleCount,
                    revision_id = d.RevisionId,
                }).ToArray(),
                count = defs.Count,
            }, s_json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
