using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

/// <summary>
/// Local, typed Studio team creation tool. Scope authority comes from
/// AgentToolRequestContext, not from LLM arguments.
///
/// The tool resolves a deterministic team id when the caller does not supply one.
/// <c>StudioTeamGAgent.HandleCreated</c> is already first-write-wins and treats a
/// repeated create for the same team id and identity-stable fields as an idempotent
/// no-op, so a stable id is what makes one logical create intent converge on a
/// single Team instead of minting a fresh actor per retry.
/// </summary>
internal sealed class CreateStudioTeamTool : IAgentTool
{
    /// <summary>
    /// Fingerprint inputs are exactly the fields <c>StudioTeamGAgent</c> treats as
    /// identity-stable (scope, display name, description). Keeping the two in step
    /// guarantees that a replay of the same intent lands on the same actor and is
    /// accepted as a no-op rather than rejected as a conflicting create.
    /// </summary>
    private const string TeamIdFingerprintSchema = "studio-team-create/v1";
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IStudioTeamProvisioningPort _teamProvisioningPort;

    public CreateStudioTeamTool(IStudioTeamProvisioningPort teamProvisioningPort)
    {
        _teamProvisioningPort = teamProvisioningPort
            ?? throw new ArgumentNullException(nameof(teamProvisioningPort));
    }

    public string Name => "aevatar_create_team";

    public string Description =>
        "Create a Studio team in the caller's current Aevatar scope. " +
        "Supply display_name and optional description or team_id; do not provide scope_id because scope is taken from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "display_name": {
              "type": "string",
              "description": "Human-readable Studio team name. Required."
            },
            "description": {
              "type": "string",
              "description": "Optional team description."
            },
            "team_id": {
              "type": "string",
              "description": "Optional caller-supplied URL-safe team id. Omit to let the service mint one."
            }
          },
          "required": ["display_name"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalPolicies.CreateScopedResource;
    public bool IsReadOnly => false;
    public bool IsDestructive => false;
    public string SideEffectKind => "studio.team.create";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio team tool uses the caller scope from the tool execution context.");
        }

        CreateStudioTeamArguments? args;
        try
        {
            var unknownArgument = FindUnknownArgument(argumentsJson);
            if (unknownArgument is not null)
                return ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = JsonSerializer.Deserialize<CreateStudioTeamArguments>(argumentsJson, s_jsonOptions);
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

        var request = new StudioTeamProvisioningRequest(scopeId, displayName)
        {
            Description = Normalize(args.Description),
            TeamId = Normalize(args.TeamId),
        };

        try
        {
            var result = await _teamProvisioningPort.CreateAsync(request, ct);
            return JsonSerializer.Serialize(
                new CreateStudioTeamResultJson(
                    Success: result.Success,
                    ScopeId: result.ScopeId,
                    TeamId: result.TeamId,
                    DisplayName: result.DisplayName,
                    Description: result.Description,
                    LifecycleStage: result.LifecycleStage,
                    MemberCount: result.MemberCount,
                    CreatedAt: result.CreatedAt,
                    UpdatedAt: result.UpdatedAt,
                    EntryMemberId: result.EntryMemberId,
                    TeamUrl: $"/api/scopes/{Uri.EscapeDataString(result.ScopeId)}/teams/{Uri.EscapeDataString(result.TeamId)}"),
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
            return ErrorJson("team_create_failed", $"Studio team creation failed: {ex.GetType().Name}");
        }
    }

    private static string ErrorJson(string code, string message) =>
        JsonSerializer.Serialize(new CreateStudioTeamErrorJson(
            new CreateStudioTeamErrorBody(code, message)),
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
            if (property.Name is not "display_name" and not "description" and not "team_id")
                return property.Name;
        }

        return null;
    }

    private sealed record CreateStudioTeamArguments(
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("team_id")] string? TeamId);

    private sealed record CreateStudioTeamResultJson(
        bool Success,
        string ScopeId,
        string TeamId,
        string DisplayName,
        string Description,
        string LifecycleStage,
        int MemberCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? EntryMemberId,
        string TeamUrl);

    private sealed record CreateStudioTeamErrorJson(CreateStudioTeamErrorBody Error);

    private sealed record CreateStudioTeamErrorBody(string Code, string Message);
}
