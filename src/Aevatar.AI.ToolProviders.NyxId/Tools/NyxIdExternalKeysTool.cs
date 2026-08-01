using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID external API keys/credentials.</summary>
public sealed class NyxIdExternalKeysTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private static readonly NyxIdClosedActionParser<NyxIdExternalKeysAction> ActionParser = new(
    [
        new("list", NyxIdExternalKeysAction.List, new(false, true, false)),
        new("rotate", NyxIdExternalKeysAction.Rotate, new(true, false, true)),
        new("delete", NyxIdExternalKeysAction.Delete, new(true, false, true)),
    ]);

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdExternalKeysTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_external_keys";

    public string Description =>
        "Manage external API keys/credentials stored in NyxID. " +
        "Actions: list, rotate (new value), delete.";

    public string ParametersSchema => $$"""
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
              "description": "External key ID (for rotate/delete)"
            },
            "credential": {
              "type": "string",
              "description": "New credential value (for rotate)"
            }
          }
        }
        """;

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
            return NyxIdClosedActionParser<NyxIdExternalKeysAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        var id = args.Str("id");

        return parsed.Action switch
        {
            NyxIdExternalKeysAction.Rotate when !string.IsNullOrWhiteSpace(id) => await RotateAsync(token, id, args, ct),
            NyxIdExternalKeysAction.Delete when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteExternalKeyAsync(token, id, ct),

            NyxIdExternalKeysAction.Rotate or NyxIdExternalKeysAction.Delete =>
                $"{{\"error\":\"'id' is required for {parsed.Name}\"}}",
            NyxIdExternalKeysAction.List => await _client.ListExternalKeysAsync(token, ct),
            _ => NyxIdClosedActionParser<NyxIdExternalKeysAction>.InvalidActionJson,
        };
    }

    private async Task<string> RotateAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var cred = args.Str("credential");
        if (string.IsNullOrWhiteSpace(cred))
            return """{"error":"'credential' is required for rotate"}""";
        return await _client.UpdateExternalKeyAsync(token, id,
            JsonSerializer.Serialize(new { credential = cred }), ct);
    }
}

internal enum NyxIdExternalKeysAction
{
    List,
    Rotate,
    Delete,
}
