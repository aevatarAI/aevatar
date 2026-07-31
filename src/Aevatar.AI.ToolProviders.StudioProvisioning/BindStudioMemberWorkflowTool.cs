using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal sealed class BindStudioMemberWorkflowTool : IStudioMutationReceiptTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IStudioMemberWorkflowBindingPort _bindingPort;

    public BindStudioMemberWorkflowTool(IStudioMemberWorkflowBindingPort bindingPort)
    {
        _bindingPort = bindingPort ?? throw new ArgumentNullException(nameof(bindingPort));
    }

    public string Name => "aevatar_bind_member_workflow";

    public string Description =>
        "Bind workflow YAML to an existing Studio member in the caller's current Aevatar scope. " +
        "Use this after creating a team/member when the workflow should appear on that member's Studio workflow page. " +
        "Supply member_id and workflow_yaml, plus optional workflow_id; do not provide scope_id because scope is taken from the session context. " +
        "The result acknowledges dispatch and includes a binding_run_url for observing completion.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "member_id": {
              "type": "string",
              "description": "Existing Studio member id to bind the workflow to. Required."
            },
            "workflow_yaml": {
              "type": "string",
              "description": "Complete workflow YAML to bind to the member. Required."
            },
            "workflow_id": {
              "type": "string",
              "description": "Optional stable workflow id. Omit to let the platform derive one."
            }
          },
          "required": ["member_id", "workflow_yaml"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalPolicies.CreateScopedResource;
    public bool IsReadOnly => false;
    public bool IsDestructive => false;
    public string SideEffectKind => "studio.member.workflow.bind";
    public string SubjectKind => "studio_member_workflow_binding";
    public string SubjectIdPropertyName => "member_id";

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.StringProperty("operation"),
        StudioQueryToolJson.StringProperty("status"),
        StudioQueryToolJson.StringProperty("member_workflow_url"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio member workflow bind tool uses the caller scope from the tool execution context.");
        }

        BindStudioMemberWorkflowArguments? args;
        try
        {
            var unknownArgument = FindUnknownArgument(argumentsJson);
            if (unknownArgument is not null)
                return ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = JsonSerializer.Deserialize<BindStudioMemberWorkflowArguments>(argumentsJson, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        if (args is null)
            return ErrorJson("invalid_arguments", "Tool arguments are required.");

        var memberId = Normalize(args.MemberId);
        if (memberId is null)
            return ErrorJson("invalid_arguments", "member_id is required.");

        var workflowYaml = Normalize(args.WorkflowYaml);
        if (workflowYaml is null)
            return ErrorJson("invalid_arguments", "workflow_yaml is required.");

        var capabilityAdmission = StudioWorkflowCapabilityToolContext.Resolve(
            ExternalCapabilityExecutionMode.Interactive);
        if (capabilityAdmission is null)
        {
            return ErrorJson(
                "caller_identity_unavailable",
                "Verified NyxID caller identity is required in AgentToolRequestContext.");
        }

        var request = new StudioMemberWorkflowBindingRequest(scopeId, memberId, workflowYaml)
        {
            WorkflowId = Normalize(args.WorkflowId),
            CapabilityAdmission = capabilityAdmission,
        };

        try
        {
            var result = await _bindingPort.BindAsync(request, ct);
            return JsonSerializer.Serialize(
                new BindStudioMemberWorkflowResultJson(
                    Success: result.Success,
                    ScopeId: result.ScopeId,
                    MemberId: result.MemberId,
                    Operation: result.Operation,
                    Status: result.Status,
                    BindingRunId: result.BindingRunId,
                    AckStage: result.AckStage,
                    BindingRunRole: result.BindingRunRole,
                    BindingRunUrl: BuildBindingRunUrl(result),
                    MemberWorkflowUrl: $"/api/scopes/{Uri.EscapeDataString(result.ScopeId)}/members/{Uri.EscapeDataString(result.MemberId)}/binding",
                    WorkflowId: result.WorkflowId,
                    RevisionId: result.RevisionId),
                s_jsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return ErrorJson("invalid_arguments", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErrorJson("member_workflow_bind_failed", $"Studio member workflow bind failed: {ex.GetType().Name}");
        }
    }

    private static string ErrorJson(string code, string message) =>
        JsonSerializer.Serialize(new BindStudioMemberWorkflowErrorJson(
            new BindStudioMemberWorkflowErrorBody(code, message)),
            s_jsonOptions);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? BuildBindingRunUrl(StudioMemberWorkflowBindingResult result) =>
        string.IsNullOrWhiteSpace(result.BindingRunId)
            ? null
            : $"/api/scopes/{Uri.EscapeDataString(result.ScopeId)}/members/{Uri.EscapeDataString(result.MemberId)}/binding-runs/{Uri.EscapeDataString(result.BindingRunId)}";

    private static string? FindUnknownArgument(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is not "member_id" and not "workflow_yaml" and not "workflow_id")
                return property.Name;
        }

        return null;
    }

    private sealed record BindStudioMemberWorkflowArguments(
        [property: JsonPropertyName("member_id")] string? MemberId,
        [property: JsonPropertyName("workflow_yaml")] string? WorkflowYaml,
        [property: JsonPropertyName("workflow_id")] string? WorkflowId);

    private sealed record BindStudioMemberWorkflowResultJson(
        bool Success,
        string ScopeId,
        string MemberId,
        string Operation,
        string Status,
        string? BindingRunId,
        string? AckStage,
        string? BindingRunRole,
        string? BindingRunUrl,
        string MemberWorkflowUrl,
        string? WorkflowId,
        string? RevisionId);

    private sealed record BindStudioMemberWorkflowErrorJson(BindStudioMemberWorkflowErrorBody Error);

    private sealed record BindStudioMemberWorkflowErrorBody(string Code, string Message);
}
