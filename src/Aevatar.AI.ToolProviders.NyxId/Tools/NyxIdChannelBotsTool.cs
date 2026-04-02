using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID channel bots and conversation routes.</summary>
public sealed class NyxIdChannelBotsTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdChannelBotsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_channel_bots";

    public string Description =>
        "Manage channel bots (Telegram, Discord, Lark, Feishu) and conversation routes. " +
        "Actions: list, show, register (platform + bot_token), delete, verify, " +
        "routes (list routes), create_route (bot_id + agent api_key_id), delete_route.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "register", "delete", "verify", "routes", "create_route", "delete_route"],
              "description": "Action to perform (default: list)"
            },
            "id": {
              "type": "string",
              "description": "Bot ID (for show/delete/verify) or route ID (for delete_route)"
            },
            "platform": {
              "type": "string",
              "enum": ["telegram", "discord", "lark", "feishu"],
              "description": "Platform (for register)"
            },
            "bot_token": {
              "type": "string",
              "description": "Bot token from the platform (for register)"
            },
            "label": {
              "type": "string",
              "description": "Label for the bot (for register)"
            },
            "bot_id": {
              "type": "string",
              "description": "Bot ID (for create_route)"
            },
            "api_key_id": {
              "type": "string",
              "description": "NyxID API key ID with callback_url (for create_route)"
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
                await _client.GetChannelBotAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteChannelBotAsync(token, id, ct),
            "verify" when !string.IsNullOrWhiteSpace(id) =>
                await _client.VerifyChannelBotAsync(token, id, ct),
            "register" => await RegisterBotAsync(token, args, ct),
            "routes" => await _client.ListConversationRoutesAsync(token, ct),
            "create_route" => await CreateRouteAsync(token, args, ct),
            "delete_route" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteConversationRouteAsync(token, id, ct),

            "show" or "delete" or "verify" or "delete_route" =>
                $"{{\"error\":\"'id' is required for {action}\"}}",
            _ => await _client.ListChannelBotsAsync(token, ct),
        };
    }

    private async Task<string> RegisterBotAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var platform = args.Str("platform");
        var botToken = args.Str("bot_token") ?? args.Str("token");
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(botToken))
            return """{"error":"'platform' and 'bot_token' are required for register"}""";

        var payload = new Dictionary<string, object?> { ["platform"] = platform, ["bot_token"] = botToken };
        var label = args.Str("label");
        if (!string.IsNullOrWhiteSpace(label)) payload["label"] = label;

        return await _client.RegisterChannelBotAsync(token, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> CreateRouteAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var botId = args.Str("bot_id");
        var apiKeyId = args.Str("api_key_id");
        if (string.IsNullOrWhiteSpace(botId) || string.IsNullOrWhiteSpace(apiKeyId))
            return """{"error":"'bot_id' and 'api_key_id' are required for create_route"}""";

        return await _client.CreateConversationRouteAsync(token,
            JsonSerializer.Serialize(new { bot_id = botId, api_key_id = apiKeyId }), ct);
    }
}
