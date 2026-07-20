using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal sealed class ListStudioTeamsTool : IAgentTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IStudioTeamQueryProvisioningPort _teamQueryPort;

    public ListStudioTeamsTool(IStudioTeamQueryProvisioningPort teamQueryPort)
    {
        _teamQueryPort = teamQueryPort ?? throw new ArgumentNullException(nameof(teamQueryPort));
    }

    public string Name => "aevatar_list_teams";

    public string Description =>
        "List Studio teams in the caller's current Aevatar scope before creating workflow resources. " +
        "Use this to find the Team the user wants; do not provide scope_id because scope is taken from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "page_size": {
              "type": "integer",
              "minimum": 1,
              "maximum": 100,
              "description": "Optional page size for Team results."
            },
            "page_token": {
              "type": "string",
              "description": "Optional continuation token from the previous response."
            }
          }
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
    public bool IsReadOnly => true;
    public bool IsDestructive => false;
    public string SideEffectKind => "studio.team.list";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = Normalize(AgentToolRequestContext.ScopeId);
        if (scopeId is null)
        {
            return ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio team list tool uses the caller scope from the tool execution context.");
        }

        ListStudioTeamsArguments? args;
        try
        {
            var normalizedArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
            var unknownArgument = FindUnknownArgument(normalizedArgumentsJson);
            if (unknownArgument is not null)
                return ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = JsonSerializer.Deserialize<ListStudioTeamsArguments>(normalizedArgumentsJson, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        args ??= new ListStudioTeamsArguments(null, null);
        var pageSize = args.PageSize;
        if (pageSize is <= 0 or > 100)
            return ErrorJson("invalid_arguments", "page_size must be between 1 and 100.");

        var request = new StudioTeamListProvisioningRequest(scopeId)
        {
            PageSize = pageSize,
            PageToken = Normalize(args.PageToken),
        };

        try
        {
            var result = await _teamQueryPort.ListAsync(request, ct);
            return JsonSerializer.Serialize(
                new ListStudioTeamsResultJson(
                    Success: result.Success,
                    ScopeId: result.ScopeId,
                    Teams: result.Teams.Select(ToTeamJson).ToList(),
                    NextPageToken: result.NextPageToken),
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
            return ErrorJson("team_list_failed", $"Studio team listing failed: {ex.GetType().Name}");
        }
    }

    private static ListStudioTeamItemJson ToTeamJson(StudioTeamProvisioningResult team) =>
        new(
            TeamId: team.TeamId,
            DisplayName: team.DisplayName,
            Description: team.Description,
            LifecycleStage: team.LifecycleStage,
            MemberCount: team.MemberCount,
            EntryMemberId: team.EntryMemberId,
            TeamUrl: $"/api/scopes/{Uri.EscapeDataString(team.ScopeId)}/teams/{Uri.EscapeDataString(team.TeamId)}");

    private static string ErrorJson(string code, string message) =>
        JsonSerializer.Serialize(new ListStudioTeamsErrorJson(
            new ListStudioTeamsErrorBody(code, message)),
            s_jsonOptions);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FindUnknownArgument(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is not "page_size" and not "page_token")
                return property.Name;
        }

        return null;
    }

    private sealed record ListStudioTeamsArguments(
        [property: JsonPropertyName("page_size")] int? PageSize,
        [property: JsonPropertyName("page_token")] string? PageToken);

    private sealed record ListStudioTeamsResultJson(
        bool Success,
        string ScopeId,
        IReadOnlyList<ListStudioTeamItemJson> Teams,
        string? NextPageToken);

    private sealed record ListStudioTeamItemJson(
        string TeamId,
        string DisplayName,
        string Description,
        string LifecycleStage,
        int MemberCount,
        string? EntryMemberId,
        string TeamUrl);

    private sealed record ListStudioTeamsErrorJson(ListStudioTeamsErrorBody Error);

    private sealed record ListStudioTeamsErrorBody(string Code, string Message);
}
