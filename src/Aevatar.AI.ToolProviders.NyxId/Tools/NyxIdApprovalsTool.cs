using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID approval requests, grants, and settings.</summary>
public sealed class NyxIdApprovalsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private static readonly NyxIdClosedActionParser<NyxIdApprovalsAction> ActionParser = new(
    [
        new("list", NyxIdApprovalsAction.List, new(false, true, false)),
        new("show", NyxIdApprovalsAction.Show, new(false, true, false)),
        new("configs", NyxIdApprovalsAction.Configs, new(false, true, false)),
        new("grants", NyxIdApprovalsAction.Grants, new(false, true, false)),
        new("enable", NyxIdApprovalsAction.Enable, new(true, false, false)),
        new("approve", NyxIdApprovalsAction.Approve, new(true, false, true)),
        new("reject", NyxIdApprovalsAction.Reject, new(true, false, true)),
        new("deny", NyxIdApprovalsAction.Deny, new(true, false, true)),
        new("revoke_grant", NyxIdApprovalsAction.RevokeGrant, new(true, false, true)),
        new("disable", NyxIdApprovalsAction.Disable, new(true, false, true)),
        new("set_config", NyxIdApprovalsAction.SetConfig, new(true, false, true)),
    ]);

    private static readonly string Schema = $$"""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": {{ActionParser.ActionNamesJson}},
              "description": "Action to perform (default: list)"
            },
            "id": {
              "type": "string",
              "description": "Request/grant/service-config ID"
            },
            "require_approval": {
              "type": "boolean",
              "description": "For set_config: require approval"
            },
            "approval_mode": {
              "type": "string",
              "enum": ["per_request", "grant"],
              "description": "For set_config: approval mode"
            }
          }
        }
        """;

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdApprovalsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_approvals";

    public string Description =>
        "Manage approval requests for proxied service calls. " +
        "Actions: list, show, approve, reject, deny, configs, grants, revoke_grant, enable, disable, set_config.";

    public string ParametersSchema => Schema;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
        ActionParser.Classify(argumentsJson);

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        var parsed = ActionParser.Parse(argumentsJson);
        return parsed.IsValid && parsed.Action is NyxIdApprovalsAction.List or NyxIdApprovalsAction.Show
            ? NyxIdManagedToolReceiptFactory.TryCreate(
                callId,
                toolName,
                resultJson,
                root => NyxIdApprovalResponseMapper.TryMap(parsed.Action, root))
            : NyxIdManagedToolReceiptFactory.TryCreate(callId, toolName, resultJson);
    }

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var parsed = ActionParser.Parse(argumentsJson);
        if (!parsed.IsValid)
            return NyxIdClosedActionParser<NyxIdApprovalsAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        var id = args.Str("id");

        return parsed.Action switch
        {
            NyxIdApprovalsAction.Show when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetApprovalAsync(token, id, ct),
            NyxIdApprovalsAction.Approve when !string.IsNullOrWhiteSpace(id) =>
                await _client.DecideApprovalAsync(token, id, true, ct),
            NyxIdApprovalsAction.Reject or NyxIdApprovalsAction.Deny when !string.IsNullOrWhiteSpace(id) =>
                await _client.DecideApprovalAsync(token, id, false, ct),
            NyxIdApprovalsAction.RevokeGrant when !string.IsNullOrWhiteSpace(id) =>
                await _client.RevokeApprovalGrantAsync(token, id, ct),
            NyxIdApprovalsAction.SetConfig when !string.IsNullOrWhiteSpace(id) =>
                await SetConfigAsync(token, id, args, ct),

            NyxIdApprovalsAction.Show or
            NyxIdApprovalsAction.Approve or
            NyxIdApprovalsAction.Reject or
            NyxIdApprovalsAction.Deny or
            NyxIdApprovalsAction.RevokeGrant or
            NyxIdApprovalsAction.SetConfig =>
                $"{{\"error\":\"'id' is required for {parsed.Name}\"}}",

            NyxIdApprovalsAction.Grants => await _client.ListApprovalGrantsAsync(token, ct),
            NyxIdApprovalsAction.Configs => await _client.ListApprovalServiceConfigsAsync(token, ct),
            NyxIdApprovalsAction.Enable => await _client.SetGlobalApprovalAsync(token, true, ct),
            NyxIdApprovalsAction.Disable => await _client.SetGlobalApprovalAsync(token, false, ct),
            NyxIdApprovalsAction.List => await _client.ListApprovalsAsync(token, ct),
            _ => NyxIdClosedActionParser<NyxIdApprovalsAction>.InvalidActionJson,
        };
    }

    private async Task<string> SetConfigAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var p = new Dictionary<string, object?>();
        var ra = args.Bool("require_approval");
        if (ra.HasValue) p["approval_required"] = ra.Value;
        var mode = args.Str("approval_mode");
        if (mode != null) p["approval_mode"] = mode;
        return await _client.SetApprovalConfigAsync(token, id, JsonSerializer.Serialize(p), ct);
    }
}

internal enum NyxIdApprovalsAction
{
    List,
    Show,
    Configs,
    Grants,
    Enable,
    Approve,
    Reject,
    Deny,
    RevokeGrant,
    Disable,
    SetConfig,
}

internal sealed record NyxIdActionDefinition<TAction>(
    string Name,
    TAction Action,
    AgentToolCallSafety Safety)
    where TAction : struct, Enum;

internal readonly record struct NyxIdParsedAction<TAction>(
    bool IsValid,
    string Name,
    TAction Action)
    where TAction : struct, Enum;

internal sealed class NyxIdClosedActionParser<TAction>
    where TAction : struct, Enum
{
    private static readonly AgentToolCallSafety InvalidSafety = new(true, false, true);
    private readonly IReadOnlyDictionary<string, NyxIdActionDefinition<TAction>> _definitions;
    private readonly string _defaultActionName;

    public NyxIdClosedActionParser(
        IEnumerable<NyxIdActionDefinition<TAction>> definitions,
        string defaultActionName = "list")
    {
        var closedDefinitions = definitions.ToDictionary(static x => x.Name, StringComparer.Ordinal);
        if (!closedDefinitions.ContainsKey(defaultActionName))
            throw new ArgumentException("A closed NyxID action set must define its default action.", nameof(defaultActionName));

        _definitions = closedDefinitions;
        _defaultActionName = defaultActionName;
        ActionNamesJson = JsonSerializer.Serialize(closedDefinitions.Keys);
    }

    public static string InvalidActionJson => """{"error":"invalid_action"}""";

    public string ActionNamesJson { get; }

    public AgentToolCallSafety Classify(string? argumentsJson)
    {
        var parsed = Parse(argumentsJson);
        return parsed.IsValid ? _definitions[parsed.Name].Safety : InvalidSafety;
    }

    public NyxIdParsedAction<TAction> Parse(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return default;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return default;

            JsonElement actionElement = default;
            var hasAction = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "action", StringComparison.OrdinalIgnoreCase))
                    continue;

                actionElement = property.Value;
                hasAction = true;
            }

            var actionName = hasAction
                ? actionElement.ValueKind == JsonValueKind.String ? actionElement.GetString() : null
                : _defaultActionName;
            if (string.IsNullOrWhiteSpace(actionName) ||
                !_definitions.TryGetValue(actionName, out var definition))
            {
                return default;
            }

            return new NyxIdParsedAction<TAction>(true, definition.Name, definition.Action);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

internal static class NyxIdManagedToolReceiptFactory
{
    private const string InvalidActionCode = "invalid_action";
    private const string InvalidArgumentsCode = "invalid_arguments";
    private const string RequestFailedCode = "nyxid_request_failed";

    public static AgentToolReceipt? TryCreate(
        string callId,
        string toolName,
        string resultJson,
        Func<JsonElement, string?>? mapSuccess = null)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return null;

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) &&
                IsError(error))
            {
                return CreateError(callId, toolName, error);
            }

            var safeResultJson = resultJson;
            if (mapSuccess is not null)
            {
                safeResultJson = mapSuccess(document.RootElement);
                if (safeResultJson is null)
                    return null;
            }

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = safeResultJson,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsError(JsonElement error) =>
        error.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(error.GetString()),
            _ => false,
        };

    private static AgentToolReceipt CreateError(
        string callId,
        string toolName,
        JsonElement error)
    {
        var providerCode = error.ValueKind == JsonValueKind.String
            ? error.GetString()?.Trim()
            : null;
        var errorCode = string.Equals(providerCode, InvalidActionCode, StringComparison.Ordinal)
            ? InvalidActionCode
            : error.ValueKind == JsonValueKind.True
                ? RequestFailedCode
                : InvalidArgumentsCode;
        var safeMessage = errorCode switch
        {
            InvalidActionCode => "The requested NyxID action is invalid.",
            InvalidArgumentsCode => "The NyxID tool arguments are invalid.",
            _ => "The NyxID request failed.",
        };
        var safeResult = errorCode == InvalidActionCode
            ? NyxIdClosedActionParser<NyxIdApprovalsAction>.InvalidActionJson
            : JsonSerializer.Serialize(new { error = errorCode, message = safeMessage });
        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = toolName ?? string.Empty,
            Status = AgentToolReceiptStatus.Error,
            ErrorCode = errorCode,
            ErrorMessage = safeMessage,
            ResultJson = safeResult,
        };
    }
}

internal static class NyxIdApprovalResponseMapper
{
    private static readonly JsonSerializerOptions SafeResultJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? TryMap(NyxIdApprovalsAction action, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        return action switch
        {
            NyxIdApprovalsAction.List => MapList(root),
            NyxIdApprovalsAction.Show => MapShow(root),
            _ => null,
        };
    }

    private static string? MapList(JsonElement root)
    {
        var response = JsonSerializer.Deserialize<NyxIdApprovalRequestsView>(root.GetRawText());
        return response?.Requests is null ||
               !response.Total.HasValue ||
               !response.Page.HasValue ||
               !response.PerPage.HasValue ||
               response.Requests.Any(static request => request?.IsValid != true)
            ? null
            : JsonSerializer.Serialize(response, SafeResultJsonOptions);
    }

    private static string? MapShow(JsonElement root)
    {
        var approval = JsonSerializer.Deserialize<NyxIdApprovalRequestView>(root.GetRawText());
        return approval?.IsValid != true
            ? null
            : JsonSerializer.Serialize(approval, SafeResultJsonOptions);
    }
}

internal sealed class NyxIdApprovalRequestsView
{
    [JsonPropertyName("requests")]
    public List<NyxIdApprovalRequestView?>? Requests { get; init; }

    [JsonPropertyName("total")]
    public ulong? Total { get; init; }

    [JsonPropertyName("page")]
    public ulong? Page { get; init; }

    [JsonPropertyName("per_page")]
    public ulong? PerPage { get; init; }
}

internal sealed class NyxIdApprovalRequestView
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("service_name")]
    public string? ServiceName { get; init; }

    [JsonPropertyName("service_slug")]
    public string? ServiceSlug { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("is_destructive")]
    public bool? IsDestructive { get; init; }

    [JsonPropertyName("approval_mode")]
    public string? ApprovalMode { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("decided_at")]
    public string? DecidedAt { get; init; }

    [JsonPropertyName("from_org_policy")]
    public bool? FromOrgPolicy { get; init; }

    [JsonPropertyName("org_name")]
    public string? OrgName { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id) &&
        !string.IsNullOrWhiteSpace(ServiceName) &&
        !string.IsNullOrWhiteSpace(ServiceSlug) &&
        !string.IsNullOrWhiteSpace(ApprovalMode) &&
        !string.IsNullOrWhiteSpace(Status) &&
        !string.IsNullOrWhiteSpace(CreatedAt) &&
        FromOrgPolicy.HasValue;
}
