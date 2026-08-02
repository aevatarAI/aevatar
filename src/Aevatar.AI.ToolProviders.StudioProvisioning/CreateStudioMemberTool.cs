using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal sealed class CreateStudioMemberTool : IStudioMutationReceiptTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IStudioMemberProvisioningPort _memberProvisioningPort;

    public CreateStudioMemberTool(IStudioMemberProvisioningPort memberProvisioningPort)
    {
        _memberProvisioningPort = memberProvisioningPort
            ?? throw new ArgumentNullException(nameof(memberProvisioningPort));
    }

    public string Name => "aevatar_create_member";

    public string Description =>
        "Create a Studio member in the caller's current Aevatar scope. " +
        "Supply display_name, implementation_kind, and optional description, member_id, or team_id; team_id is required for workflow members. " +
        "Do not provide scope_id because scope is taken from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "display_name": {
              "type": "string",
              "description": "Human-readable Studio member name. Required."
            },
            "implementation_kind": {
              "type": "string",
              "enum": ["workflow", "script", "gagent"],
              "description": "Member implementation kind. Required."
            },
            "description": {
              "type": "string",
              "description": "Optional member description."
            },
            "member_id": {
              "type": "string",
              "description": "Optional caller-supplied URL-safe member id. Omit to let the service mint one."
            },
            "team_id": {
              "type": "string",
              "description": "Existing Studio team id to assign this member to. Required when implementation_kind is workflow."
            }
          },
          "required": ["display_name", "implementation_kind"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalPolicies.CreateScopedResource;
    public bool IsReadOnly => false;
    public bool IsDestructive => false;
    public string SideEffectKind => "studio.member.create";
    public string SubjectKind => "studio_member";
    public string SubjectIdPropertyName => "member_id";

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.StringProperty("display_name"),
        StudioQueryToolJson.StringProperty("implementation_kind"),
        StudioQueryToolJson.StringProperty("lifecycle_stage"),
        StudioQueryToolJson.StringProperty("published_service_id"),
        StudioQueryToolJson.StringProperty("member_url"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio member tool uses the caller scope from the tool execution context.");
        }

        CreateStudioMemberArguments? args;
        try
        {
            var unknownArgument = FindUnknownArgument(argumentsJson);
            if (unknownArgument is not null)
                return ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = JsonSerializer.Deserialize<CreateStudioMemberArguments>(argumentsJson, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        if (args is null)
            return ErrorJson("invalid_arguments", "Tool arguments are required.");

        var displayName = Normalize(args.DisplayName);
        if (displayName is null)
            return ErrorJson("invalid_arguments", "display_name is required.");

        var implementationKind = Normalize(args.ImplementationKind);
        if (implementationKind is null)
            return ErrorJson("invalid_arguments", "implementation_kind is required.");

        var teamId = Normalize(args.TeamId);
        if (string.Equals(implementationKind, "workflow", StringComparison.OrdinalIgnoreCase) && teamId is null)
            return ErrorJson("invalid_arguments", "team_id is required for workflow members.");

        var request = new StudioMemberProvisioningRequest(scopeId, displayName, implementationKind)
        {
            Description = Normalize(args.Description),
            MemberId = Normalize(args.MemberId),
            TeamId = teamId,
        };

        try
        {
            var result = await _memberProvisioningPort.CreateAsync(request, ct);
            return JsonSerializer.Serialize(
                new CreateStudioMemberResultJson(
                    Success: result.Success,
                    ScopeId: result.ScopeId,
                    MemberId: result.MemberId,
                    DisplayName: result.DisplayName,
                    Description: result.Description,
                    ImplementationKind: result.ImplementationKind,
                    LifecycleStage: result.LifecycleStage,
                    PublishedServiceId: result.PublishedServiceId,
                    LastBoundRevisionId: result.LastBoundRevisionId,
                    TeamId: result.TeamId,
                    CreatedAt: result.CreatedAt,
                    UpdatedAt: result.UpdatedAt,
                    MemberUrl: $"/api/scopes/{Uri.EscapeDataString(result.ScopeId)}/members/{Uri.EscapeDataString(result.MemberId)}"),
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
            return ErrorJson("member_create_failed", $"Studio member creation failed: {ex.GetType().Name}");
        }
    }

    private static string ErrorJson(string code, string message) =>
        JsonSerializer.Serialize(new CreateStudioMemberErrorJson(
            new CreateStudioMemberErrorBody(code, message)),
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
            if (property.Name is not "display_name" and not "implementation_kind" and not "description" and not "member_id" and not "team_id")
                return property.Name;
        }

        return null;
    }

    private sealed record CreateStudioMemberArguments(
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("implementation_kind")] string? ImplementationKind,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("member_id")] string? MemberId,
        [property: JsonPropertyName("team_id")] string? TeamId);

    private sealed record CreateStudioMemberResultJson(
        bool Success,
        string ScopeId,
        string MemberId,
        string DisplayName,
        string Description,
        string ImplementationKind,
        string LifecycleStage,
        string PublishedServiceId,
        string? LastBoundRevisionId,
        string? TeamId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string MemberUrl);

    private sealed record CreateStudioMemberErrorJson(CreateStudioMemberErrorBody Error);

    private sealed record CreateStudioMemberErrorBody(string Code, string Message);
}
