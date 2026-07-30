using System.Text.Json;
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
        if (ra.HasValue) p["require_approval"] = ra.Value;
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

    public NyxIdClosedActionParser(IEnumerable<NyxIdActionDefinition<TAction>> definitions)
    {
        var closedDefinitions = definitions.ToDictionary(static x => x.Name, StringComparer.Ordinal);
        if (!closedDefinitions.ContainsKey("list"))
            throw new ArgumentException("A closed NyxID action set must define the default list action.", nameof(definitions));

        _definitions = closedDefinitions;
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
                : "list";
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
