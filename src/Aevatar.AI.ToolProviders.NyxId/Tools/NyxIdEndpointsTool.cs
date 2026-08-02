using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID user endpoints (service base URLs).</summary>
public sealed class NyxIdEndpointsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private static readonly NyxIdClosedActionParser<NyxIdEndpointsAction> ActionParser = new(
    [
        new("list", NyxIdEndpointsAction.List, new(false, true, false)),
        new("update", NyxIdEndpointsAction.Update, new(true, false, false)),
        new("delete", NyxIdEndpointsAction.Delete, new(true, false, true)),
    ]);

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdEndpointsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_endpoints";

    public string Description =>
        "Manage user endpoints (service base URLs). " +
        "Actions: list, update, delete.";

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
              "description": "Endpoint ID (for update/delete)"
            },
            "url": {
              "type": "string",
              "description": "New URL (for update)"
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
            return NyxIdClosedActionParser<NyxIdEndpointsAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        var id = args.Str("id");

        return parsed.Action switch
        {
            NyxIdEndpointsAction.Update when !string.IsNullOrWhiteSpace(id) => await UpdateAsync(token, id, args, ct),
            NyxIdEndpointsAction.Delete when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteEndpointAsync(token, id, ct),

            NyxIdEndpointsAction.Update or NyxIdEndpointsAction.Delete =>
                $"{{\"error\":\"'id' is required for {parsed.Name}\"}}",
            NyxIdEndpointsAction.List => await _client.ListEndpointsAsync(token, ct),
            _ => NyxIdClosedActionParser<NyxIdEndpointsAction>.InvalidActionJson,
        };
    }

    private async Task<string> UpdateAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var url = args.Str("url");
        if (string.IsNullOrWhiteSpace(url))
            return """{"error":"'url' is required for update"}""";
        return await _client.UpdateEndpointAsync(token, id, JsonSerializer.Serialize(new { url }), ct);
    }
}

internal enum NyxIdEndpointsAction
{
    List,
    Update,
    Delete,
}
