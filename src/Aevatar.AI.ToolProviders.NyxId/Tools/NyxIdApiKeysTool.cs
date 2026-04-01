using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID API keys for programmatic access.</summary>
public sealed class NyxIdApiKeysTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdApiKeysTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_api_keys";

    public string Description =>
        "Manage NyxID API keys. " +
        "Actions: list, show, create, rotate, delete, update.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "create", "rotate", "delete", "update"],
              "description": "Action to perform (default: list)"
            },
            "id": {
              "type": "string",
              "description": "API key ID (for show/rotate/delete/update)"
            },
            "name": {
              "type": "string",
              "description": "Key name (required for create)"
            },
            "scopes": {
              "type": "string",
              "description": "Space-separated scopes (e.g. 'proxy read write')"
            },
            "allowed_services": {
              "type": "string",
              "description": "Comma-separated service IDs"
            },
            "allowed_nodes": {
              "type": "string",
              "description": "Comma-separated node IDs"
            },
            "allow_all_services": {
              "type": "boolean",
              "description": "Allow all services"
            },
            "allow_all_nodes": {
              "type": "boolean",
              "description": "Allow all nodes"
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        var action = args.Str("action", "list");
        var id = args.Str("id");

        return action switch
        {
            "show" when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetApiKeyAsync(token, id, ct),
            "rotate" when !string.IsNullOrWhiteSpace(id) =>
                await _client.RotateApiKeyAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteApiKeyAsync(token, id, ct),
            "update" when !string.IsNullOrWhiteSpace(id) =>
                await UpdateKeyAsync(token, id, args, ct),
            "create" => await CreateKeyAsync(token, args, ct),

            "show" or "rotate" or "delete" or "update" =>
                $"{{\"error\":\"'id' is required for {action}\"}}",
            _ => await _client.ListApiKeysAsync(token, ct),
        };
    }

    private async Task<string> CreateKeyAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var name = args.Str("name");
        if (string.IsNullOrWhiteSpace(name))
            return """{"error":"'name' is required for create"}""";
        return await _client.CreateApiKeyAsync(token, JsonSerializer.Serialize(BuildPayload(args, name)), ct);
    }

    private async Task<string> UpdateKeyAsync(string token, string id, ToolArgs args, CancellationToken ct) =>
        await _client.UpdateApiKeyAsync(token, id, JsonSerializer.Serialize(BuildPayload(args, args.Str("name"))), ct);

    private static Dictionary<string, object?> BuildPayload(ToolArgs args, string? name)
    {
        var p = new Dictionary<string, object?>();
        if (name != null) p["name"] = name;
        var scopes = args.Str("scopes");
        if (scopes != null) p["scopes"] = scopes;
        var svc = args.Str("allowed_services");
        if (svc != null) p["allowed_service_ids"] = svc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nodes = args.Str("allowed_nodes");
        if (nodes != null) p["allowed_node_ids"] = nodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var aas = args.Bool("allow_all_services");
        if (aas.HasValue) p["allow_all_services"] = aas.Value;
        var aan = args.Bool("allow_all_nodes");
        if (aan.HasValue) p["allow_all_nodes"] = aan.Value;
        return p;
    }
}
