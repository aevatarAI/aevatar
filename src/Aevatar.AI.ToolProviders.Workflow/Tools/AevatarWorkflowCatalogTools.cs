using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

internal sealed class ListAevatarWorkflowTemplatesTool : IAgentTool
{
    private readonly IWorkflowCatalogPort _catalog;

    public ListAevatarWorkflowTemplatesTool(IWorkflowCatalogPort catalog)
    {
        _catalog = catalog;
    }

    public string Name => "aevatar_list_workflow_templates";

    public string Description =>
        "List public workflow templates in the global Aevatar template library. " +
        "This does not list workflows owned by Teams in the caller's workspace.";

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

            var publicTemplates = (await _catalog.ListPublicWorkflowCatalogAsync(ct)).ToArray();
            return WorkflowCatalogToolJson.Serialize(
                new WorkflowTemplateCatalogListJson(
                    publicTemplates.Select(WorkflowCatalogToolJson.ToJson).ToArray(),
                    publicTemplates.Length));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WorkflowCatalogToolJson.Error("workflow_template_query_failed", ex.GetType().Name);
        }
    }
}

internal sealed class GetAevatarWorkflowTemplateTool : IAgentTool
{
    private static readonly string[] s_allowedProperties = ["template_name"];
    private readonly IWorkflowCatalogPort _catalog;

    public GetAevatarWorkflowTemplateTool(IWorkflowCatalogPort catalog)
    {
        _catalog = catalog;
    }

    public string Name => "aevatar_get_workflow_template";

    public string Description =>
        "Get a public workflow template by exact name from the global Aevatar template library. " +
        "This does not read a workflow owned by a Team in the caller's workspace.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "template_name": {
              "type": "string",
              "description": "Public workflow template name"
            }
          },
          "required": ["template_name"],
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
                    "template_name",
                    out var templateName,
                    out error))
            {
                return WorkflowCatalogToolJson.Error("invalid_arguments", error);
            }

            var detail = await _catalog.GetPublicWorkflowDetailAsync(templateName, ct);
            return detail is null
                ? WorkflowCatalogToolJson.Error(
                    "workflow_template_not_found",
                    $"Workflow template '{templateName}' was not found in the global Aevatar template library.")
                : WorkflowCatalogToolJson.Serialize(WorkflowCatalogToolJson.ToJson(detail));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WorkflowCatalogToolJson.Error("workflow_template_query_failed", ex.GetType().Name);
        }
    }
}

internal sealed record WorkflowTemplateCatalogListJson(
    IReadOnlyList<WorkflowTemplateCatalogItemJson> Templates,
    int Count);

internal sealed record WorkflowTemplateCatalogItemJson(
    string Name,
    string Description,
    string Category,
    string Group,
    string GroupLabel,
    int SortOrder,
    string Source,
    string SourceLabel,
    bool ShowInLibrary,
    bool IsPrimitiveExample,
    bool RequiresLlmProvider,
    IReadOnlyList<string> Primitives,
    long AuthorityStateVersion,
    DateTimeOffset ProjectionWatermark,
    string LastEventId);

internal sealed record WorkflowTemplateCatalogDetailJson(
    WorkflowTemplateCatalogItemJson Template,
    string Yaml,
    WorkflowTemplateCatalogDefinitionJson Definition,
    IReadOnlyList<WorkflowTemplateCatalogEdgeJson> Edges);

internal sealed record WorkflowTemplateCatalogDefinitionJson(
    string Name,
    string Description,
    bool ClosedWorldMode,
    IReadOnlyList<WorkflowTemplateCatalogRoleJson> Roles,
    IReadOnlyList<WorkflowTemplateCatalogStepJson> Steps);

internal sealed record WorkflowTemplateCatalogRoleJson(
    string Id,
    string Name,
    string SystemPrompt,
    string Provider,
    string Model,
    float? Temperature,
    int? MaxTokens,
    int? MaxToolRounds,
    int? MaxHistoryMessages,
    IReadOnlyList<string> EventModules,
    string EventRoutes,
    IReadOnlyList<string> Connectors);

internal sealed record WorkflowTemplateCatalogStepJson(
    string Id,
    string Type,
    string TargetRole,
    IReadOnlyDictionary<string, string> Parameters,
    string Next,
    IReadOnlyDictionary<string, string> Branches,
    IReadOnlyList<WorkflowTemplateCatalogChildStepJson> Children);

internal sealed record WorkflowTemplateCatalogChildStepJson(
    string Id,
    string Type,
    string TargetRole);

internal sealed record WorkflowTemplateCatalogEdgeJson(
    string From,
    string To,
    string Label);

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

    public static WorkflowTemplateCatalogItemJson ToJson(WorkflowCatalogItem item) =>
        new(
            item.Name,
            item.Description,
            item.Category,
            item.Group,
            item.GroupLabel,
            item.SortOrder,
            item.Source,
            item.SourceLabel,
            item.ShowInLibrary,
            item.IsPrimitiveExample,
            item.RequiresLlmProvider,
            item.Primitives.ToArray(),
            item.AuthorityStateVersion,
            item.ProjectionWatermark,
            item.LastEventId);

    public static WorkflowTemplateCatalogDetailJson ToJson(WorkflowCatalogItemDetail detail) =>
        new(
            ToJson(detail.Catalog),
            detail.Yaml,
            ToJson(detail.Definition),
            detail.Edges.Select(ToJson).ToArray());

    private static WorkflowTemplateCatalogDefinitionJson ToJson(WorkflowCatalogDefinition definition) =>
        new(
            definition.Name,
            definition.Description,
            definition.ClosedWorldMode,
            definition.Roles.Select(ToJson).ToArray(),
            definition.Steps.Select(ToJson).ToArray());

    private static WorkflowTemplateCatalogRoleJson ToJson(WorkflowCatalogRole role) =>
        new(
            role.Id,
            role.Name,
            role.SystemPrompt,
            role.Provider,
            role.Model,
            role.Temperature,
            role.MaxTokens,
            role.MaxToolRounds,
            role.MaxHistoryMessages,
            role.EventModules.ToArray(),
            role.EventRoutes,
            role.Connectors.ToArray());

    private static WorkflowTemplateCatalogStepJson ToJson(WorkflowCatalogStep step) =>
        new(
            step.Id,
            step.Type,
            step.TargetRole,
            new Dictionary<string, string>(step.Parameters, StringComparer.Ordinal),
            step.Next,
            new Dictionary<string, string>(step.Branches, StringComparer.Ordinal),
            step.Children.Select(ToJson).ToArray());

    private static WorkflowTemplateCatalogChildStepJson ToJson(WorkflowCatalogChildStep child) =>
        new(child.Id, child.Type, child.TargetRole);

    private static WorkflowTemplateCatalogEdgeJson ToJson(WorkflowCatalogEdge edge) =>
        new(edge.From, edge.To, edge.Label);

    private sealed record WorkflowCatalogToolErrorJson(WorkflowCatalogToolErrorBody Error);

    private sealed record WorkflowCatalogToolErrorBody(string Code, string Message);
}
