using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID on-premise node agents.</summary>
public sealed class NyxIdNodesTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private static readonly NyxIdClosedActionParser<NyxIdNodesAction> ActionParser = new(
    [
        new("list", NyxIdNodesAction.List, new(false, true, false)),
        new("show", NyxIdNodesAction.Show, new(false, true, false)),
        new("delete", NyxIdNodesAction.Delete, new(true, false, true)),
        new("register_token", NyxIdNodesAction.RegisterToken, new(true, false, false)),
        new("rotate_token", NyxIdNodesAction.RotateToken, new(true, false, true)),
    ]);

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdNodesTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_nodes";

    public string Description =>
        "Manage on-premise node agents. " +
        "Actions: list, show, delete, register_token, rotate_token.";

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
              "description": "Node ID (for show/delete/rotate_token)"
            },
            "name": {
              "type": "string",
              "description": "Node name (for register_token)"
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
            return NyxIdClosedActionParser<NyxIdNodesAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        var id = args.Str("id");

        return parsed.Action switch
        {
            NyxIdNodesAction.Show when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetNodeAsync(token, id, ct),
            NyxIdNodesAction.Delete when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteNodeAsync(token, id, ct),
            NyxIdNodesAction.RotateToken when !string.IsNullOrWhiteSpace(id) =>
                await _client.RotateNodeTokenAsync(token, id, ct),
            NyxIdNodesAction.RegisterToken => await RegisterTokenAsync(token, args, ct),

            NyxIdNodesAction.Show or NyxIdNodesAction.Delete or NyxIdNodesAction.RotateToken =>
                $"{{\"error\":\"'id' is required for {parsed.Name}\"}}",
            NyxIdNodesAction.List => await _client.ListNodesAsync(token, ct),
            _ => NyxIdClosedActionParser<NyxIdNodesAction>.InvalidActionJson,
        };
    }

    private async Task<string> RegisterTokenAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var name = args.Str("name");
        if (string.IsNullOrWhiteSpace(name))
            return """{"error":"'name' is required for register_token"}""";
        return await _client.GenerateNodeRegistrationTokenAsync(token,
            JsonSerializer.Serialize(new { name }), ct);
    }
}

internal enum NyxIdNodesAction
{
    List,
    Show,
    Delete,
    RegisterToken,
    RotateToken,
}
