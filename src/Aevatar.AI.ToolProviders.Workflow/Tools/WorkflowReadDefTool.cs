using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Ports;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Reads the full YAML content of a workflow definition.
/// </summary>
public sealed class WorkflowReadDefTool : IAgentTool
{
    private readonly IWorkflowDefinitionCommandAdapter _definitionCommand;

    public WorkflowReadDefTool(IWorkflowDefinitionCommandAdapter definitionCommand)
    {
        _definitionCommand = definitionCommand;
    }

    public string Name => "workflow_read_def";

    public string Description =>
        "Read the full YAML content of a workflow definition.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "workflow_name": {
              "type": "string",
              "description": "Name of the workflow to read"
            }
          },
          "required": ["workflow_name"]
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
            var name = args.Str("workflow_name");

            if (string.IsNullOrWhiteSpace(name))
                return """{"error":"'workflow_name' is required"}""";

            var snapshot = await _definitionCommand.GetDefinitionAsync(name, ct);
            if (snapshot is null)
                return JsonSerializer.Serialize(new { error = $"Workflow definition '{name}' not found" });

            return JsonSerializer.Serialize(new
            {
                name = snapshot.Name,
                yaml = snapshot.Yaml,
                revision_id = snapshot.RevisionId,
                last_modified = snapshot.LastModified,
            }, s_json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
