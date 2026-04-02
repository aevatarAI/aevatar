using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Ports;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Creates a new workflow definition from YAML. The YAML is validated before saving.
/// </summary>
public sealed class WorkflowCreateDefTool : IAgentTool
{
    private readonly IWorkflowDefinitionCommandAdapter _definitionCommand;
    private readonly WorkflowToolOptions _options;

    public WorkflowCreateDefTool(
        IWorkflowDefinitionCommandAdapter definitionCommand,
        WorkflowToolOptions options)
    {
        _definitionCommand = definitionCommand;
        _options = options;
    }

    public string Name => "workflow_create_def";

    public string Description =>
        "Create a new workflow definition from YAML. The YAML is validated before saving.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "workflow_name": {
              "type": "string",
              "description": "Name for the new workflow definition"
            },
            "yaml": {
              "type": "string",
              "description": "YAML content of the workflow definition"
            }
          },
          "required": ["workflow_name", "yaml"]
        }
        """;

    public bool IsReadOnly => false;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

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
            var yaml = args.Str("yaml");

            if (string.IsNullOrWhiteSpace(name))
                return """{"error":"'workflow_name' is required"}""";
            if (string.IsNullOrWhiteSpace(yaml))
                return """{"error":"'yaml' is required"}""";
            if (yaml.Length > _options.MaxYamlSizeChars)
                return JsonSerializer.Serialize(new { error = $"YAML content exceeds maximum size of {_options.MaxYamlSizeChars} characters" });

            var result = await _definitionCommand.CreateAsync(name, yaml, ct);

            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                name = result.Name,
                revision_id = result.RevisionId,
                diagnostics = result.Diagnostics.Select(d => new
                {
                    severity = d.Severity,
                    message = d.Message,
                    step_id = d.StepId,
                    field = d.Field,
                }).ToArray(),
            }, s_json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
