using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Ports;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>
/// Updates an existing workflow definition with new YAML.
/// Requires the expected revision ID for optimistic concurrency.
/// </summary>
public sealed class WorkflowUpdateDefTool : IAgentTool
{
    private readonly IWorkflowDefinitionCommandAdapter _definitionCommand;
    private readonly WorkflowToolOptions _options;

    public WorkflowUpdateDefTool(
        IWorkflowDefinitionCommandAdapter definitionCommand,
        WorkflowToolOptions options)
    {
        _definitionCommand = definitionCommand;
        _options = options;
    }

    public string Name => "workflow_update_def";

    public string Description =>
        "Update an existing workflow definition with new YAML. " +
        "Requires the expected revision ID for optimistic concurrency.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "workflow_name": {
              "type": "string",
              "description": "Name of the workflow definition to update"
            },
            "yaml": {
              "type": "string",
              "description": "New YAML content for the workflow definition"
            },
            "expected_revision": {
              "type": "string",
              "description": "Expected revision ID for optimistic concurrency check"
            }
          },
          "required": ["workflow_name", "yaml", "expected_revision"]
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
            var expectedRevision = args.Str("expected_revision");

            if (string.IsNullOrWhiteSpace(name))
                return """{"error":"'workflow_name' is required"}""";
            if (string.IsNullOrWhiteSpace(yaml))
                return """{"error":"'yaml' is required"}""";
            if (string.IsNullOrWhiteSpace(expectedRevision))
                return """{"error":"'expected_revision' is required"}""";
            if (yaml.Length > _options.MaxYamlSizeChars)
                return JsonSerializer.Serialize(new { error = $"YAML content exceeds maximum size of {_options.MaxYamlSizeChars} characters" });

            var result = await _definitionCommand.UpdateAsync(name, yaml, expectedRevision, ct);

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
