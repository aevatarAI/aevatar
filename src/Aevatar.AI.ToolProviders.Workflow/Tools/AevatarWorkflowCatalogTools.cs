using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

internal sealed class ListAevatarWorkflowsTool : IAgentTool
{
    private readonly IWorkflowCatalogPort _catalog;

    public ListAevatarWorkflowsTool(IWorkflowCatalogPort catalog)
    {
        _catalog = catalog;
    }

    public string Name => "aevatar_list_workflows";

    public string Description =>
        "List workflows in the global runnable workflow catalog. This does not list Studio member drafts.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """;

    public bool IsReadOnly => true;

    public bool IsDestructive => false;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!WorkflowCatalogToolJson.TryParseObject(argumentsJson, [], out _, out var error))
                return WorkflowCatalogToolJson.Error("invalid_arguments", error);

            var workflows = await _catalog.ListWorkflowCatalogAsync(ct);
            return WorkflowCatalogToolJson.Serialize(new
            {
                workflows,
                count = workflows.Count,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WorkflowCatalogToolJson.Error("workflow_query_failed", ex.GetType().Name);
        }
    }
}

internal sealed class GetAevatarWorkflowTool : IAgentTool
{
    private static readonly string[] s_allowedProperties = ["workflow_name"];
    private readonly IWorkflowCatalogPort _catalog;

    public GetAevatarWorkflowTool(IWorkflowCatalogPort catalog)
    {
        _catalog = catalog;
    }

    public string Name => "aevatar_get_workflow";

    public string Description =>
        "Get a workflow by name from the global runnable workflow catalog. This does not read a Studio member draft.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "workflow_name": {
              "type": "string",
              "description": "Global runnable workflow catalog name"
            }
          },
          "required": ["workflow_name"],
          "additionalProperties": false
        }
        """;

    public bool IsReadOnly => true;

    public bool IsDestructive => false;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!WorkflowCatalogToolJson.TryParseObject(
                    argumentsJson,
                    s_allowedProperties,
                    out var arguments,
                    out var error))
            {
                return WorkflowCatalogToolJson.Error("invalid_arguments", error);
            }

            if (!WorkflowCatalogToolJson.TryGetRequiredString(
                    arguments,
                    "workflow_name",
                    out var workflowName,
                    out error))
            {
                return WorkflowCatalogToolJson.Error("invalid_arguments", error);
            }

            var detail = await _catalog.GetWorkflowDetailAsync(workflowName, ct);
            return detail is null
                ? WorkflowCatalogToolJson.Error(
                    "workflow_not_found",
                    $"Workflow '{workflowName}' was not found in the global runnable workflow catalog.")
                : WorkflowCatalogToolJson.Serialize(detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WorkflowCatalogToolJson.Error("workflow_query_failed", ex.GetType().Name);
        }
    }
}

internal static class WorkflowCatalogToolJson
{
    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static bool TryParseObject(
        string? argumentsJson,
        IReadOnlyCollection<string> allowedProperties,
        out JsonElement arguments,
        out string error)
    {
        var normalizedJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;

        try
        {
            using var document = JsonDocument.Parse(normalizedJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                arguments = default;
                error = "Arguments must be a JSON object.";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal))
                {
                    arguments = default;
                    error = $"Unknown argument '{property.Name}'.";
                    return false;
                }
            }

            arguments = document.RootElement.Clone();
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            arguments = default;
            error = "Arguments must be valid JSON.";
            return false;
        }
    }

    public static bool TryGetRequiredString(
        JsonElement arguments,
        string propertyName,
        out string value,
        out string error)
    {
        if (!arguments.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = string.Empty;
            error = $"'{propertyName}' must be a non-empty string.";
            return false;
        }

        value = property.GetString()!.Trim();
        error = string.Empty;
        return true;
    }

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, s_serializerOptions);

    public static string Error(string code, string message) =>
        Serialize(new WorkflowCatalogToolErrorJson(new WorkflowCatalogToolErrorBody(code, message)));

    private sealed record WorkflowCatalogToolErrorJson(WorkflowCatalogToolErrorBody Error);

    private sealed record WorkflowCatalogToolErrorBody(string Code, string Message);
}
