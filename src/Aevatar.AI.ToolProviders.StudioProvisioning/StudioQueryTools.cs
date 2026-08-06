using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal interface IStudioReceiptTool : IAgentTool
{
    AgentToolReceipt? IAgentTool.CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson) =>
        this switch
        {
            IStudioReadOnlyReceiptTool readOnlyTool => StudioQueryToolJson.CreateReadOnlyResultReceipt(
                Name,
                callId,
                toolName,
                resultJson,
                readOnlyTool.ResultRequirements),
            IStudioMutationReceiptTool mutationTool => StudioQueryToolJson.CreateMutationResultReceipt(
                Name,
                SideEffectKind,
                mutationTool.SubjectKind,
                callId,
                toolName,
                resultJson,
                mutationTool.SuccessStatusPropertyName,
                mutationTool.SuccessStatusValue,
                mutationTool.SubjectIdPropertyName,
                mutationTool.ResultRequirements),
            IStudioMutationErrorReceiptTool => StudioQueryToolJson.CreateMutationErrorResultReceipt(
                Name,
                SideEffectKind,
                callId,
                toolName,
                resultJson),
            _ => null,
        };
}

internal interface IStudioReadOnlyReceiptTool : IStudioReceiptTool
{
    IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; }
}

internal interface IStudioMutationReceiptTool : IStudioReceiptTool
{
    string SubjectKind { get; }

    string? SuccessStatusPropertyName => null;

    string? SuccessStatusValue => null;

    string SubjectIdPropertyName { get; }

    IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; }
}

internal interface IStudioMutationErrorReceiptTool : IStudioReceiptTool
{
}

internal sealed class ListStudioTeamsTool : IStudioReadOnlyReceiptTool
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

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.ArrayProperty("teams"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
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

internal sealed class GetStudioTeamTool : IStudioReadOnlyReceiptTool
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

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.StringProperty("team_id"),
        StudioQueryToolJson.StringProperty("display_name"),
        StudioQueryToolJson.StringProperty("lifecycle_stage"),
        StudioQueryToolJson.StringProperty("team_url"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
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

internal sealed class ListStudioMembersTool : IStudioReadOnlyReceiptTool
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

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.ArrayProperty("members"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
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

internal sealed class GetStudioMemberTool : IStudioReadOnlyReceiptTool
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

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.ObjectProperty("summary"),
        StudioQueryToolJson.StringProperty("summary", "scope_id"),
        StudioQueryToolJson.StringProperty("summary", "member_id"),
        StudioQueryToolJson.StringProperty("summary", "display_name"),
        StudioQueryToolJson.StringProperty("summary", "implementation_kind"),
        StudioQueryToolJson.StringProperty("summary", "member_url"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
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

internal sealed class ListStudioSchedulesTool : IStudioReadOnlyReceiptTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = StudioQueryToolJson.Options;
    private readonly IStudioMemberAutomationQueryPort _schedules;

    public ListStudioSchedulesTool(IStudioMemberAutomationQueryPort schedules)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
    }

    public string Name => "aevatar_list_schedules";

    public string Description =>
        "List workflow schedules owned by a Studio team or one Studio member in the caller's current Aevatar scope. " +
        "Supply team_id plus optional member_id, page_size, page_token, and include_total_count; do not provide scope_id because scope is taken from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "team_id": {
              "type": "string",
              "description": "Studio team id that owns the member. Required."
            },
            "member_id": {
              "type": "string",
              "description": "Optional Studio member id whose schedules should be read. Omit to list schedules for the whole team."
            },
            "page_size": {
              "type": "integer",
              "description": "Optional maximum number of schedules to return."
            },
            "page_token": {
              "type": "string",
              "description": "Optional continuation token returned by a previous list call."
            },
            "include_total_count": {
              "type": "boolean",
              "description": "Optional flag requesting a total_count when the read model can provide it."
            }
          },
          "required": ["team_id"]
        }
        """;

    public bool IsReadOnly => true;
    public bool IsDestructive => false;

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.StringProperty("team_id"),
        StudioQueryToolJson.ArrayProperty("schedules"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return StudioQueryToolJson.ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio schedule query tool uses the caller scope from the tool execution context.");
        }

        ListStudioSchedulesArguments? args;
        try
        {
            var unknownArgument = StudioQueryToolJson.FindUnknownArgument(
                argumentsJson,
                ["team_id", "member_id", "page_size", "page_token", "include_total_count"]);
            if (unknownArgument is not null)
                return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = StudioQueryToolJson.Deserialize<ListStudioSchedulesArguments>(argumentsJson);
        }
        catch (JsonException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        var teamId = StudioQueryToolJson.Normalize(args?.TeamId);
        if (teamId is null)
            return StudioQueryToolJson.ErrorJson("invalid_arguments", "team_id is required.");

        var memberId = StudioQueryToolJson.Normalize(args?.MemberId);

        try
        {
            var result = await _schedules.ListAsync(
                scopeId,
                teamId,
                memberId,
                args?.PageSize ?? 50,
                StudioQueryToolJson.Normalize(args?.PageToken),
                args?.IncludeTotalCount ?? false,
                ct);
            return JsonSerializer.Serialize(
                new ListStudioSchedulesResultJson(
                    scopeId,
                    teamId,
                    memberId,
                    result.Items.Select(StudioScheduleResultJson.From).ToArray(),
                    result.NextCursor,
                    result.TotalCount),
                s_jsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StudioQueryToolJson.ErrorJson("schedule_query_failed", $"Studio schedule query failed: {ex.GetType().Name}");
        }
    }

    private sealed record ListStudioSchedulesArguments(
        [property: JsonPropertyName("team_id")] string? TeamId,
        [property: JsonPropertyName("member_id")] string? MemberId,
        [property: JsonPropertyName("page_size")] int? PageSize,
        [property: JsonPropertyName("page_token")] string? PageToken,
        [property: JsonPropertyName("include_total_count")] bool? IncludeTotalCount);

    private sealed record ListStudioSchedulesResultJson(
        string ScopeId,
        string TeamId,
        string? MemberId,
        IReadOnlyList<StudioScheduleResultJson> Schedules,
        string? NextPageToken,
        long? TotalCount);
}

internal sealed class GetStudioScheduleTool : IStudioReadOnlyReceiptTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = StudioQueryToolJson.Options;
    private readonly IStudioMemberAutomationQueryPort _schedules;

    public GetStudioScheduleTool(IStudioMemberAutomationQueryPort schedules)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
    }

    public string Name => "aevatar_get_schedule";

    public string Description =>
        "Get one workflow schedule owned by a Studio member in the caller's current Aevatar scope. " +
        "Supply team_id, member_id, and schedule_id; do not provide scope_id because scope is taken from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "team_id": {
              "type": "string",
              "description": "Studio team id that owns the member. Required."
            },
            "member_id": {
              "type": "string",
              "description": "Studio member id that owns the schedule. Required."
            },
            "schedule_id": {
              "type": "string",
              "description": "Schedule id to read. Required."
            }
          },
          "required": ["team_id", "member_id", "schedule_id"]
        }
        """;

    public bool IsReadOnly => true;
    public bool IsDestructive => false;

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.StringProperty("team_id"),
        StudioQueryToolJson.StringProperty("member_id"),
        StudioQueryToolJson.StringProperty("schedule_id"),
        StudioQueryToolJson.StringProperty("published_service_id"),
        StudioQueryToolJson.StringProperty("schedule_cron"),
        StudioQueryToolJson.StringProperty("schedule_timezone"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return StudioQueryToolJson.ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio schedule query tool uses the caller scope from the tool execution context.");
        }

        GetStudioScheduleArguments? args;
        try
        {
            var unknownArgument = StudioQueryToolJson.FindUnknownArgument(
                argumentsJson,
                ["team_id", "member_id", "schedule_id"]);
            if (unknownArgument is not null)
                return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = StudioQueryToolJson.Deserialize<GetStudioScheduleArguments>(argumentsJson);
        }
        catch (JsonException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        var teamId = StudioQueryToolJson.Normalize(args?.TeamId);
        if (teamId is null)
            return StudioQueryToolJson.ErrorJson("invalid_arguments", "team_id is required.");

        var memberId = StudioQueryToolJson.Normalize(args?.MemberId);
        if (memberId is null)
            return StudioQueryToolJson.ErrorJson("invalid_arguments", "member_id is required.");

        var scheduleId = StudioQueryToolJson.Normalize(args?.ScheduleId);
        if (scheduleId is null)
            return StudioQueryToolJson.ErrorJson("invalid_arguments", "schedule_id is required.");

        try
        {
            var schedule = await _schedules.GetAsync(
                scopeId,
                teamId,
                memberId,
                scheduleId,
                ct);
            if (schedule is null)
                return StudioQueryToolJson.ErrorJson("schedule_not_found", $"Studio schedule '{scheduleId}' was not found for member '{memberId}' in team '{teamId}' and scope '{scopeId}'.");

            return JsonSerializer.Serialize(StudioScheduleResultJson.From(schedule), s_jsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return StudioQueryToolJson.ErrorJson("invalid_arguments", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StudioQueryToolJson.ErrorJson("schedule_query_failed", $"Studio schedule query failed: {ex.GetType().Name}");
        }
    }

    private sealed record GetStudioScheduleArguments(
        [property: JsonPropertyName("team_id")] string? TeamId,
        [property: JsonPropertyName("member_id")] string? MemberId,
        [property: JsonPropertyName("schedule_id")] string? ScheduleId);
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

    public static AgentToolReceipt? CreateReadOnlyResultReceipt(
        string defaultToolName,
        string callId,
        string toolName,
        string resultJson,
        IReadOnlyCollection<ResultPropertyRequirement> resultRequirements)
    {
        if (!TryParseObject(resultJson, out var document))
            return null;

        using (document)
        {
            var root = document.RootElement;
            if (TryReadError(root, out var errorCode, out var errorMessage))
            {
                return new AgentToolReceipt
                {
                    CallId = callId ?? string.Empty,
                    ToolName = ResolveToolName(defaultToolName, toolName),
                    Status = AgentToolReceiptStatus.Error,
                    ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage,
                    ResultJson = resultJson ?? string.Empty,
                };
            }

            if (!HasRequiredProperties(root, resultRequirements))
                return null;

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = ResolveToolName(defaultToolName, toolName),
                Status = AgentToolReceiptStatus.Success,
                ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                ResultJson = resultJson ?? string.Empty,
            };
        }
    }

    public static AgentToolReceipt? CreateMutationErrorResultReceipt(
        string defaultToolName,
        string sideEffectKind,
        string callId,
        string toolName,
        string resultJson)
    {
        if (!TryParseObject(resultJson, out var document))
            return null;

        using (document)
        {
            if (!TryReadError(document.RootElement, out var errorCode, out var errorMessage))
                return null;

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = ResolveToolName(defaultToolName, toolName),
                Status = AgentToolReceiptStatus.Error,
                ApprovalMode = AgentToolReceiptApprovalMode.Unspecified,
                SideEffectKind = sideEffectKind,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                ResultJson = resultJson ?? string.Empty,
            };
        }
    }

    public static AgentToolReceipt? CreateMutationResultReceipt(
        string defaultToolName,
        string sideEffectKind,
        string subjectKind,
        string callId,
        string toolName,
        string resultJson,
        string? successStatusPropertyName,
        string? successStatusValue,
        string subjectIdPropertyName,
        IReadOnlyCollection<ResultPropertyRequirement> resultRequirements) =>
        CreateSuccessfulMutationResultReceipt(
            defaultToolName,
            sideEffectKind,
            subjectKind,
            callId,
            toolName,
            resultJson,
            successStatusPropertyName,
            successStatusValue,
            subjectIdPropertyName,
            resultRequirements);

    private static AgentToolReceipt? CreateSuccessfulMutationResultReceipt(
        string defaultToolName,
        string sideEffectKind,
        string subjectKind,
        string callId,
        string toolName,
        string resultJson,
        string? successStatusPropertyName,
        string? successStatusValue,
        string subjectIdPropertyName,
        IReadOnlyCollection<ResultPropertyRequirement> resultRequirements)
    {
        if (!TryParseObject(resultJson, out var document))
            return null;

        using (document)
        {
            var root = document.RootElement;
            if (TryReadError(root, out var errorCode, out var errorMessage))
            {
                return new AgentToolReceipt
                {
                    CallId = callId ?? string.Empty,
                    ToolName = ResolveToolName(defaultToolName, toolName),
                    Status = AgentToolReceiptStatus.Error,
                    ApprovalMode = AgentToolReceiptApprovalMode.Unspecified,
                    SideEffectKind = sideEffectKind,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage,
                    ResultJson = resultJson ?? string.Empty,
                };
            }

            if (!IsVerifiedMutationSuccess(root, successStatusPropertyName, successStatusValue) ||
                !HasRequiredProperties(root, resultRequirements))
            {
                return null;
            }

            var subjectId = TryGetString(root, subjectIdPropertyName);
            if (string.IsNullOrWhiteSpace(subjectId))
                return null;

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = ResolveToolName(defaultToolName, toolName),
                Status = AgentToolReceiptStatus.Success,
                ApprovalMode = AgentToolReceiptApprovalMode.Unspecified,
                SideEffectKind = sideEffectKind,
                SubjectKind = subjectKind,
                SubjectId = subjectId,
                ResultJson = resultJson ?? string.Empty,
            };
        }
    }

    public static ResultPropertyRequirement StringProperty(params string[] path) =>
        new(path, JsonValueKind.String);

    public static ResultPropertyRequirement ArrayProperty(params string[] path) =>
        new(path, JsonValueKind.Array);

    public static ResultPropertyRequirement ObjectProperty(params string[] path) =>
        new(path, JsonValueKind.Object);

    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeArgumentsJson(string argumentsJson) =>
        string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;

    private static bool TryParseObject(string? resultJson, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(resultJson))
            return false;

        try
        {
            document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return true;

            document.Dispose();
            document = null!;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadError(JsonElement root, out string errorCode, out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;

        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return false;

        errorCode = TryGetString(error, "code") ?? string.Empty;
        errorMessage = TryGetString(error, "message") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(errorCode) || !string.IsNullOrWhiteSpace(errorMessage);
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasRequiredProperties(
        JsonElement element,
        IReadOnlyCollection<ResultPropertyRequirement> resultRequirements)
    {
        foreach (var resultRequirement in resultRequirements)
        {
            if (!TryGetProperty(element, resultRequirement.Path, out var property) ||
                property.ValueKind != resultRequirement.ValueKind)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetProperty(
        JsonElement element,
        IReadOnlyList<string> path,
        out JsonElement property)
    {
        property = element;
        foreach (var segment in path)
        {
            if (property.ValueKind != JsonValueKind.Object || !property.TryGetProperty(segment, out property))
                return false;
        }

        return true;
    }

    private static bool IsVerifiedMutationSuccess(
        JsonElement root,
        string? successStatusPropertyName,
        string? successStatusValue)
    {
        if (!string.IsNullOrWhiteSpace(successStatusPropertyName) &&
            !string.IsNullOrWhiteSpace(successStatusValue))
        {
            return string.Equals(
                TryGetString(root, successStatusPropertyName),
                successStatusValue,
                StringComparison.Ordinal);
        }

        return TryGetBoolean(root, "success", out var success) && success;
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName, out bool result)
    {
        result = false;
        if (!element.TryGetProperty(propertyName, out var value) ||
            (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
        {
            return false;
        }

        result = value.GetBoolean();
        return true;
    }

    private static string ResolveToolName(string defaultToolName, string toolName) =>
        string.IsNullOrWhiteSpace(toolName) ? defaultToolName : toolName;

    public sealed record ResultPropertyRequirement(
        IReadOnlyList<string> Path,
        JsonValueKind ValueKind);

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

internal sealed record StudioScheduleResultJson(
    string ScopeId,
    string TeamId,
    string MemberId,
    string ScheduleId,
    string PublishedServiceId,
    string DisplayName,
    string Prompt,
    string ScheduleCron,
    string ScheduleTimezone,
    bool Enabled,
    string AuthorizationStatus,
    DateTimeOffset? CredentialExpiresAtUtc,
    string LastAuthorizationErrorCode,
    string OperationId,
    long CredentialGeneration,
    bool RevocationPending,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? LastFireAt,
    long StateVersion,
    string CredentialSourceKind,
    DateTimeOffset UpdatedAt,
    string ScheduleUrl)
{
    public static StudioScheduleResultJson From(StudioMemberAutomationView item) =>
        new(
            item.ScopeId,
            item.TeamId,
            item.MemberId,
            item.ScheduleId,
            item.PublishedServiceId,
            item.DisplayName,
            item.Prompt,
            item.ScheduleCron,
            item.ScheduleTimezone,
            item.Enabled,
            item.AuthorizationStatus,
            item.CredentialExpiresAtUtc,
            item.LastAuthorizationErrorCode,
            item.OperationId,
            item.CredentialGeneration,
            item.RevocationPending,
            item.NextFireAt,
            item.LastFireAt,
            item.StateVersion,
            item.CredentialSourceKind,
            item.UpdatedAt,
            $"/api/schedules/{Uri.EscapeDataString(item.ScheduleId)}" +
            $"?ownerKind={Uri.EscapeDataString(ScheduledDispatchOwnerKinds.StudioMemberAutomation)}" +
            $"&ownerScopeId={Uri.EscapeDataString(item.ScopeId)}" +
            $"&ownerTeamId={Uri.EscapeDataString(item.TeamId)}" +
            $"&ownerMemberId={Uri.EscapeDataString(item.MemberId)}");
}
