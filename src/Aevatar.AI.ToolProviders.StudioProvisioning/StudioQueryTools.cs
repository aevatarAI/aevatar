using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal sealed class ListStudioTeamsTool : IAgentTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = StudioQueryToolJson.Options;
    private readonly IStudioTeamQueryPort _teamQueryPort;

    public ListStudioTeamsTool(IStudioTeamQueryPort teamQueryPort)
    {
        _teamQueryPort = teamQueryPort ?? throw new ArgumentNullException(nameof(teamQueryPort));
    }

    public string Name => "aevatar_list_teams";

    public string Description =>
        "List Studio teams in the caller's current Aevatar scope. " +
        "Optionally supply page_size and page_token; do not provide scope_id because scope is taken from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "page_size": {
              "type": "integer",
              "description": "Optional maximum number of teams to return."
            },
            "page_token": {
              "type": "string",
              "description": "Optional continuation token returned by a previous list call."
            }
          }
        }
        """;

    public bool IsReadOnly => true;
    public bool IsDestructive => false;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioQueryToolJson.Normalize(AgentToolRequestContext.ScopeId);
        if (scopeId is null)
        {
            return StudioQueryToolJson.ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio team query tool uses the caller scope from the tool execution context.");
        }

        ListStudioTeamsArguments? args;
        try
        {
            var unknownArgument = StudioQueryToolJson.FindUnknownArgument(argumentsJson, ["page_size", "page_token"]);
            if (unknownArgument is not null)
                return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = StudioQueryToolJson.Deserialize<ListStudioTeamsArguments>(argumentsJson);
        }
        catch (JsonException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        try
        {
            var page = args is null || (args.PageSize is null && string.IsNullOrWhiteSpace(args.PageToken))
                ? null
                : new StudioTeamRosterPageRequest(args.PageSize, StudioQueryToolJson.Normalize(args.PageToken));
            var result = await _teamQueryPort.ListAsync(scopeId, page, ct);
            return JsonSerializer.Serialize(
                new ListStudioTeamsResultJson(
                    result.ScopeId,
                    result.Teams.Select(StudioTeamResultJson.From).ToArray(),
                    result.NextPageToken),
                s_jsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StudioQueryToolJson.ErrorJson("team_query_failed", $"Studio team query failed: {ex.GetType().Name}");
        }
    }

    private sealed record ListStudioTeamsArguments(
        [property: JsonPropertyName("page_size")] int? PageSize,
        [property: JsonPropertyName("page_token")] string? PageToken);

    private sealed record ListStudioTeamsResultJson(
        string ScopeId,
        IReadOnlyList<StudioTeamResultJson> Teams,
        string? NextPageToken);
}

internal sealed class GetStudioTeamTool : IAgentTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = StudioQueryToolJson.Options;
    private readonly IStudioTeamQueryPort _teamQueryPort;

    public GetStudioTeamTool(IStudioTeamQueryPort teamQueryPort)
    {
        _teamQueryPort = teamQueryPort ?? throw new ArgumentNullException(nameof(teamQueryPort));
    }

    public string Name => "aevatar_get_team";

    public string Description =>
        "Get one Studio team from the caller's current Aevatar scope. " +
        "Supply team_id; do not provide scope_id because scope is taken from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "team_id": {
              "type": "string",
              "description": "Studio team id to read. Required."
            }
          },
          "required": ["team_id"]
        }
        """;

    public bool IsReadOnly => true;
    public bool IsDestructive => false;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioQueryToolJson.Normalize(AgentToolRequestContext.ScopeId);
        if (scopeId is null)
        {
            return StudioQueryToolJson.ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio team query tool uses the caller scope from the tool execution context.");
        }

        GetStudioTeamArguments? args;
        try
        {
            var unknownArgument = StudioQueryToolJson.FindUnknownArgument(argumentsJson, ["team_id"]);
            if (unknownArgument is not null)
                return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = StudioQueryToolJson.Deserialize<GetStudioTeamArguments>(argumentsJson);
        }
        catch (JsonException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        var teamId = StudioQueryToolJson.Normalize(args?.TeamId);
        if (teamId is null)
            return StudioQueryToolJson.ErrorJson("invalid_arguments", "team_id is required.");

        try
        {
            var team = await _teamQueryPort.GetAsync(scopeId, teamId, ct);
            if (team is null)
                return StudioQueryToolJson.ErrorJson("team_not_found", $"Studio team '{teamId}' was not found in scope '{scopeId}'.");

            return JsonSerializer.Serialize(StudioTeamResultJson.From(team), s_jsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StudioQueryToolJson.ErrorJson("team_query_failed", $"Studio team query failed: {ex.GetType().Name}");
        }
    }

    private sealed record GetStudioTeamArguments(
        [property: JsonPropertyName("team_id")] string? TeamId);
}

internal sealed class ListStudioMembersTool : IAgentTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = StudioQueryToolJson.Options;
    private readonly IStudioMemberQueryPort _memberQueryPort;

    public ListStudioMembersTool(IStudioMemberQueryPort memberQueryPort)
    {
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
    }

    public string Name => "aevatar_list_members";

    public string Description =>
        "List Studio members in the caller's current Aevatar scope. " +
        "Optionally supply team_id, page_size, and page_token; do not provide scope_id because scope is taken from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "team_id": {
              "type": "string",
              "description": "Optional Studio team id to filter members by team assignment."
            },
            "page_size": {
              "type": "integer",
              "description": "Optional maximum number of members to return."
            },
            "page_token": {
              "type": "string",
              "description": "Optional continuation token returned by a previous list call."
            }
          }
        }
        """;

    public bool IsReadOnly => true;
    public bool IsDestructive => false;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioQueryToolJson.Normalize(AgentToolRequestContext.ScopeId);
        if (scopeId is null)
        {
            return StudioQueryToolJson.ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio member query tool uses the caller scope from the tool execution context.");
        }

        ListStudioMembersArguments? args;
        try
        {
            var unknownArgument = StudioQueryToolJson.FindUnknownArgument(argumentsJson, ["team_id", "page_size", "page_token"]);
            if (unknownArgument is not null)
                return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = StudioQueryToolJson.Deserialize<ListStudioMembersArguments>(argumentsJson);
        }
        catch (JsonException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        try
        {
            var page = args is null
                || (args.PageSize is null
                    && string.IsNullOrWhiteSpace(args.PageToken)
                    && string.IsNullOrWhiteSpace(args.TeamId))
                ? null
                : new StudioMemberRosterPageRequest(
                    args.PageSize,
                    StudioQueryToolJson.Normalize(args.PageToken),
                    StudioQueryToolJson.Normalize(args.TeamId));
            var result = await _memberQueryPort.ListAsync(scopeId, page, ct);
            return JsonSerializer.Serialize(
                new ListStudioMembersResultJson(
                    result.ScopeId,
                    result.Members.Select(StudioMemberSummaryResultJson.From).ToArray(),
                    result.NextPageToken),
                s_jsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StudioQueryToolJson.ErrorJson("member_query_failed", $"Studio member query failed: {ex.GetType().Name}");
        }
    }

    private sealed record ListStudioMembersArguments(
        [property: JsonPropertyName("team_id")] string? TeamId,
        [property: JsonPropertyName("page_size")] int? PageSize,
        [property: JsonPropertyName("page_token")] string? PageToken);

    private sealed record ListStudioMembersResultJson(
        string ScopeId,
        IReadOnlyList<StudioMemberSummaryResultJson> Members,
        string? NextPageToken);
}

internal sealed class GetStudioMemberTool : IAgentTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = StudioQueryToolJson.Options;
    private readonly IStudioMemberQueryPort _memberQueryPort;

    public GetStudioMemberTool(IStudioMemberQueryPort memberQueryPort)
    {
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
    }

    public string Name => "aevatar_get_member";

    public string Description =>
        "Get one Studio member from the caller's current Aevatar scope. " +
        "Supply member_id; do not provide scope_id because scope is taken from the session context. " +
        "The response preserves member_id, published_service_id, and workflow implementation identity as separate fields.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "member_id": {
              "type": "string",
              "description": "Studio member id to read. Required."
            }
          },
          "required": ["member_id"]
        }
        """;

    public bool IsReadOnly => true;
    public bool IsDestructive => false;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioQueryToolJson.Normalize(AgentToolRequestContext.ScopeId);
        if (scopeId is null)
        {
            return StudioQueryToolJson.ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio member query tool uses the caller scope from the tool execution context.");
        }

        GetStudioMemberArguments? args;
        try
        {
            var unknownArgument = StudioQueryToolJson.FindUnknownArgument(argumentsJson, ["member_id"]);
            if (unknownArgument is not null)
                return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = StudioQueryToolJson.Deserialize<GetStudioMemberArguments>(argumentsJson);
        }
        catch (JsonException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        var memberId = StudioQueryToolJson.Normalize(args?.MemberId);
        if (memberId is null)
            return StudioQueryToolJson.ErrorJson("invalid_arguments", "member_id is required.");

        try
        {
            var member = await _memberQueryPort.GetAsync(scopeId, memberId, ct);
            if (member is null)
                return StudioQueryToolJson.ErrorJson("member_not_found", $"Studio member '{memberId}' was not found in scope '{scopeId}'.");

            return JsonSerializer.Serialize(StudioMemberDetailResultJson.From(member), s_jsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StudioQueryToolJson.ErrorJson("member_query_failed", $"Studio member query failed: {ex.GetType().Name}");
        }
    }

    private sealed record GetStudioMemberArguments(
        [property: JsonPropertyName("member_id")] string? MemberId);
}

internal static class StudioQueryToolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static T? Deserialize<T>(string argumentsJson) =>
        JsonSerializer.Deserialize<T>(NormalizeArgumentsJson(argumentsJson), Options);

    public static string? FindUnknownArgument(string argumentsJson, IReadOnlyCollection<string> allowedNames)
    {
        using var document = JsonDocument.Parse(NormalizeArgumentsJson(argumentsJson));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Tool arguments must be a JSON object.");

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!allowedNames.Contains(property.Name))
                return property.Name;
        }

        return null;
    }

    public static string ErrorJson(string code, string message) =>
        JsonSerializer.Serialize(new StudioQueryToolErrorJson(
                new StudioQueryToolErrorBody(code, message)),
            Options);

    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeArgumentsJson(string argumentsJson) =>
        string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;

    private sealed record StudioQueryToolErrorJson(StudioQueryToolErrorBody Error);

    private sealed record StudioQueryToolErrorBody(string Code, string Message);
}

internal sealed record StudioTeamResultJson(
    string ScopeId,
    string TeamId,
    string DisplayName,
    string Description,
    string LifecycleStage,
    int MemberCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? EntryMemberId,
    string TeamUrl)
{
    public static StudioTeamResultJson From(StudioTeamSummaryResponse team) =>
        new(
            team.ScopeId,
            team.TeamId,
            team.DisplayName,
            team.Description,
            team.LifecycleStage,
            team.MemberCount,
            team.CreatedAt,
            team.UpdatedAt,
            team.EntryMemberId,
            $"/api/scopes/{Uri.EscapeDataString(team.ScopeId)}/teams/{Uri.EscapeDataString(team.TeamId)}");
}

internal sealed record StudioMemberSummaryResultJson(
    string ScopeId,
    string MemberId,
    string DisplayName,
    string Description,
    string ImplementationKind,
    string LifecycleStage,
    string PublishedServiceId,
    string? LastBoundRevisionId,
    string? TeamId,
    StudioMemberImplementationRefResponse? ImplementationRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string MemberUrl,
    string BindingUrl)
{
    public static StudioMemberSummaryResultJson From(StudioMemberSummaryResponse member) =>
        new(
            member.ScopeId,
            member.MemberId,
            member.DisplayName,
            member.Description,
            member.ImplementationKind,
            member.LifecycleStage,
            member.PublishedServiceId,
            member.LastBoundRevisionId,
            member.TeamId,
            member.ImplementationRef,
            member.CreatedAt,
            member.UpdatedAt,
            $"/api/scopes/{Uri.EscapeDataString(member.ScopeId)}/members/{Uri.EscapeDataString(member.MemberId)}",
            $"/api/scopes/{Uri.EscapeDataString(member.ScopeId)}/members/{Uri.EscapeDataString(member.MemberId)}/binding");
}

internal sealed record StudioMemberDetailResultJson(
    StudioMemberSummaryResultJson Summary,
    StudioMemberImplementationRefResponse? ImplementationRef,
    StudioMemberBindingContractResponse? LastBinding,
    StudioMemberBindingRunStatusResponse? CurrentBindingRun)
{
    public static StudioMemberDetailResultJson From(StudioMemberDetailResponse member) =>
        new(
            StudioMemberSummaryResultJson.From(member.Summary),
            member.ImplementationRef,
            member.LastBinding,
            member.CurrentBindingRun);
}
