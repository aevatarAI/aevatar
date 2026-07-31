using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal sealed class CreateStudioMemberWorkflowDraftTool : IStudioMutationErrorReceiptTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IStudioMemberWorkflowDraftProvisioningPort _provisioningPort;

    public CreateStudioMemberWorkflowDraftTool(
        IStudioMemberWorkflowDraftProvisioningPort provisioningPort)
    {
        _provisioningPort = provisioningPort
            ?? throw new ArgumentNullException(nameof(provisioningPort));
    }

    public string Name => "aevatar_create_member_workflow_draft";

    public string Description =>
        "Create or reuse a Studio workflow member and save an editable workflow draft in the caller's current Aevatar scope. " +
        "Use this when authoring can describe the intended workflow but an exact NyxID operation is not yet available. " +
        "The draft is not runnable and this tool does not bind, schedule, publish, or run it. " +
        "Supply team_id, display_name, workflow_yaml, and optional member_id or workflow_id; scope comes from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "team_id": {
              "type": "string",
              "description": "Existing Studio Team id that owns the workflow member. Required."
            },
            "display_name": {
              "type": "string",
              "description": "Human-readable workflow member name. Required."
            },
            "workflow_yaml": {
              "type": "string",
              "description": "Complete editable workflow YAML. Required."
            },
            "member_id": {
              "type": "string",
              "description": "Optional existing or stable workflow member id."
            },
            "workflow_id": {
              "type": "string",
              "description": "Optional stable draft workflow id."
            }
          },
          "required": ["team_id", "display_name", "workflow_yaml"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalPolicies.CreateScopedResource;
    public bool IsReadOnly => false;
    public bool IsDestructive => false;
    public string SideEffectKind => "studio.workflow_draft.create";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio workflow draft tool uses the caller scope from the tool execution context.");
        }

        CreateStudioMemberWorkflowDraftArguments? args;
        try
        {
            var unknownArgument = FindUnknownArgument(argumentsJson);
            if (unknownArgument is not null)
                return ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = JsonSerializer.Deserialize<CreateStudioMemberWorkflowDraftArguments>(
                argumentsJson,
                s_jsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        if (args is null)
            return ErrorJson("invalid_arguments", "Tool arguments are required.");

        var teamId = Normalize(args.TeamId);
        if (teamId is null)
            return ErrorJson("invalid_arguments", "team_id is required.");

        var displayName = Normalize(args.DisplayName);
        if (displayName is null)
            return ErrorJson("invalid_arguments", "display_name is required.");

        var workflowYaml = Normalize(args.WorkflowYaml);
        if (workflowYaml is null)
            return ErrorJson("invalid_arguments", "workflow_yaml is required.");

        var request = new StudioMemberWorkflowDraftProvisioningRequest(
            scopeId,
            teamId,
            displayName,
            workflowYaml)
        {
            MemberId = Normalize(args.MemberId),
            WorkflowId = Normalize(args.WorkflowId),
        };

        try
        {
            var result = await _provisioningPort.SaveAsync(request, ct);
            return JsonSerializer.Serialize(result, s_jsonOptions);
        }
        catch (StudioMemberWorkflowDraftProvisioningException ex)
        {
            return ErrorJson(ex.Code, ex.Message, ex.MemberId);
        }
        catch (InvalidOperationException ex)
        {
            return ErrorJson("invalid_arguments", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ErrorJson(
                "workflow_draft_create_failed",
                "Studio member workflow draft creation failed.");
        }
    }

    private static string ErrorJson(string code, string message, string? memberId = null) =>
        JsonSerializer.Serialize(
            new CreateStudioMemberWorkflowDraftErrorJson(
                new CreateStudioMemberWorkflowDraftErrorBody(code, message, memberId)),
            s_jsonOptions);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FindUnknownArgument(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is not "team_id" and
                not "display_name" and
                not "workflow_yaml" and
                not "member_id" and
                not "workflow_id")
            {
                return property.Name;
            }
        }

        return null;
    }

    private sealed record CreateStudioMemberWorkflowDraftArguments(
        [property: JsonPropertyName("team_id")] string? TeamId,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("workflow_yaml")] string? WorkflowYaml,
        [property: JsonPropertyName("member_id")] string? MemberId,
        [property: JsonPropertyName("workflow_id")] string? WorkflowId);

    private sealed record CreateStudioMemberWorkflowDraftErrorJson(
        CreateStudioMemberWorkflowDraftErrorBody Error);

    private sealed record CreateStudioMemberWorkflowDraftErrorBody(
        string Code,
        string Message,
        string? MemberId);
}
