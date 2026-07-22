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
            // Enumerate only entries published to the shared library, the same public-catalog
            // contract the Console enforces (apps/aevatar-console-web/src/shared/workflows/
            // catalogVisibility.ts filters showInLibrary). This keeps internal primitive/demo
            // examples out of the agent-facing listing. Scope-owned private definitions never reach
            // this readmodel — they are excluded at projection time — so what remains is the public
            // template gallery, addressable in full detail only by exact name via aevatar_get_workflow.
            var publicTemplates = workflows.Where(item => item.ShowInLibrary).ToArray();
            return WorkflowCatalogToolJson.Serialize(
                new WorkflowCatalogListJson(
                    publicTemplates.Select(WorkflowCatalogToolJson.ToJson).ToArray(),
                    publicTemplates.Length));
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
                : WorkflowCatalogToolJson.Serialize(WorkflowCatalogToolJson.ToJson(detail));
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

internal sealed record WorkflowCatalogListJson(
    IReadOnlyList<WorkflowCatalogItemJson> Workflows,
    int Count);

internal sealed record WorkflowCatalogItemJson(
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

internal sealed record WorkflowCatalogDetailJson(
    WorkflowCatalogItemJson Catalog,
    string Yaml,
    WorkflowCatalogDefinitionJson Definition,
    IReadOnlyList<WorkflowCatalogEdgeJson> Edges);

internal sealed record WorkflowCatalogDefinitionJson(
    string Name,
    string Description,
    bool ClosedWorldMode,
    IReadOnlyList<WorkflowCatalogRoleJson> Roles,
    IReadOnlyList<WorkflowCatalogStepJson> Steps);

internal sealed record WorkflowCatalogRoleJson(
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

internal sealed record WorkflowCatalogStepJson(
    string Id,
    string Type,
    string TargetRole,
    IReadOnlyDictionary<string, string> Parameters,
    string Next,
    IReadOnlyDictionary<string, string> Branches,
    IReadOnlyList<WorkflowCatalogChildStepJson> Children);

internal sealed record WorkflowCatalogChildStepJson(
    string Id,
    string Type,
    string TargetRole);

internal sealed record WorkflowCatalogEdgeJson(
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

    public static WorkflowCatalogItemJson ToJson(WorkflowCatalogItem item) =>
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

    public static WorkflowCatalogDetailJson ToJson(WorkflowCatalogItemDetail detail) =>
        new(
            ToJson(detail.Catalog),
            detail.Yaml,
            ToJson(detail.Definition),
            detail.Edges.Select(ToJson).ToArray());

    private static WorkflowCatalogDefinitionJson ToJson(WorkflowCatalogDefinition definition) =>
        new(
            definition.Name,
            definition.Description,
            definition.ClosedWorldMode,
            definition.Roles.Select(ToJson).ToArray(),
            definition.Steps.Select(ToJson).ToArray());

    private static WorkflowCatalogRoleJson ToJson(WorkflowCatalogRole role) =>
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

    private static WorkflowCatalogStepJson ToJson(WorkflowCatalogStep step) =>
        new(
            step.Id,
            step.Type,
            step.TargetRole,
            new Dictionary<string, string>(step.Parameters, StringComparer.Ordinal),
            step.Next,
            new Dictionary<string, string>(step.Branches, StringComparer.Ordinal),
            step.Children.Select(ToJson).ToArray());

    private static WorkflowCatalogChildStepJson ToJson(WorkflowCatalogChildStep child) =>
        new(child.Id, child.Type, child.TargetRole);

    private static WorkflowCatalogEdgeJson ToJson(WorkflowCatalogEdge edge) =>
        new(edge.From, edge.To, edge.Label);

    private sealed record WorkflowCatalogToolErrorJson(WorkflowCatalogToolErrorBody Error);

    private sealed record WorkflowCatalogToolErrorBody(string Code, string Message);
}
