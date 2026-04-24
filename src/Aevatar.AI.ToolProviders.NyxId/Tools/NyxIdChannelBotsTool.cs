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
        "Manage NyxID-native channel bots (Telegram, Discord, Lark, Feishu) and conversation routes. " +
        "Bot actions: list, show, register, delete, verify. " +
        "Route actions: routes, show_route, create_route, update_route, delete_route. " +
        "Use this tool to inspect existing Nyx bot/route state or to register Nyx-native fields such as Lark verification_token. " +
        "Supports per-sender routing in group chats.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "register", "delete", "verify", "routes", "show_route", "create_route", "update_route", "delete_route"],
              "description": "Action to perform (default: list)"
            },
            "id": {
              "type": "string",
              "description": "Bot ID (for show/delete/verify) or route ID (for show_route/update_route/delete_route)"
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
            "app_id": {
              "type": "string",
              "description": "Lark/Feishu app ID (for register with platform=lark/feishu)"
            },
            "app_secret": {
              "type": "string",
              "description": "Lark/Feishu app secret (for register with platform=lark/feishu)"
            },
            "verification_token": {
              "type": "string",
              "description": "Lark/Feishu verification token (for register when required by the backend)"
            },
            "public_key": {
              "type": "string",
              "description": "Ed25519 public key hex (for register with platform=discord)"
            },
            "channel_bot_id": {
              "type": "string",
              "description": "Bot ID (for routes list filter or create_route)"
            },
            "agent_api_key_id": {
              "type": "string",
              "description": "NyxID API key ID with callback_url configured (for create_route/update_route)"
            },
            "platform_conversation_id": {
              "type": "string",
              "description": "Platform chat ID, or '*' for default/wildcard (for create_route)"
            },
            "platform_conversation_type": {
              "type": "string",
              "enum": ["private", "group", "channel"],
              "description": "Conversation type (for create_route, default: private)"
            },
            "sender_id": {
              "type": "string",
              "description": "Platform sender/user ID for per-user routing in group chats (for create_route)"
            },
            "default_agent": {
              "type": "boolean",
              "description": "Make this the default agent for the bot (for create_route/update_route)"
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

            "routes" => await ListRoutesAsync(token, args, ct),
            "show_route" when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetConversationRouteAsync(token, id, ct),
            "create_route" => await CreateRouteAsync(token, args, ct),
            "update_route" when !string.IsNullOrWhiteSpace(id) =>
                await UpdateRouteAsync(token, id, args, ct),
            "delete_route" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteConversationRouteAsync(token, id, ct),

            "show" or "delete" or "verify" or "show_route" or "update_route" or "delete_route" =>
                $"{{\"error\":\"'id' is required for {action}\"}}",
            _ => await _client.ListChannelBotsAsync(token, ct),
        };
    }

    private async Task<string> RegisterBotAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var platform = args.Str("platform");
        if (string.IsNullOrWhiteSpace(platform))
            return """{"error":"'platform' is required for register"}""";

        var payload = new Dictionary<string, object?> { ["platform"] = platform };

        // Pass through all credential fields — server validates platform-specific requirements
        var botToken = args.Str("bot_token") ?? args.Str("token");
        if (!string.IsNullOrWhiteSpace(botToken)) payload["bot_token"] = botToken;

        var label = args.Str("label");
        if (!string.IsNullOrWhiteSpace(label)) payload["label"] = label;

        var appId = args.Str("app_id");
        if (!string.IsNullOrWhiteSpace(appId)) payload["app_id"] = appId;

        var appSecret = args.Str("app_secret");
        if (!string.IsNullOrWhiteSpace(appSecret)) payload["app_secret"] = appSecret;

        var verificationToken = args.Str("verification_token");
        if (!string.IsNullOrWhiteSpace(verificationToken)) payload["verification_token"] = verificationToken;

        var publicKey = args.Str("public_key");
        if (!string.IsNullOrWhiteSpace(publicKey)) payload["public_key"] = publicKey;

        // Basic sanity: need at least one credential field
        if (string.IsNullOrWhiteSpace(botToken) && string.IsNullOrWhiteSpace(appId))
            return """{"error":"At least 'bot_token' or 'app_id' is required for register. Server validates platform-specific requirements."}""";

        return await _client.RegisterChannelBotAsync(token, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> ListRoutesAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var botId = args.Str("channel_bot_id") ?? args.Str("bot_id");
        return await _client.ListConversationRoutesAsync(token, botId, ct);
    }

    private async Task<string> CreateRouteAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var botId = args.Str("channel_bot_id") ?? args.Str("bot_id");
        var apiKeyId = args.Str("agent_api_key_id") ?? args.Str("api_key_id");
        if (string.IsNullOrWhiteSpace(botId) || string.IsNullOrWhiteSpace(apiKeyId))
            return """{"error":"'channel_bot_id' and 'agent_api_key_id' are required for create_route"}""";

        var payload = new Dictionary<string, object?>
        {
            ["channel_bot_id"] = botId,
            ["agent_api_key_id"] = apiKeyId,
        };

        var convId = args.Str("platform_conversation_id");
        if (!string.IsNullOrWhiteSpace(convId)) payload["platform_conversation_id"] = convId;

        var convType = args.Str("platform_conversation_type");
        if (!string.IsNullOrWhiteSpace(convType)) payload["platform_conversation_type"] = convType;

        var senderId = args.Str("sender_id");
        if (!string.IsNullOrWhiteSpace(senderId)) payload["sender_id"] = senderId;

        var defaultAgent = args.Bool("default_agent");
        if (defaultAgent.HasValue) payload["default_agent"] = defaultAgent.Value;

        return await _client.CreateConversationRouteAsync(token, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> UpdateRouteAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();

        var apiKeyId = args.Str("agent_api_key_id") ?? args.Str("api_key_id");
        if (!string.IsNullOrWhiteSpace(apiKeyId)) payload["agent_api_key_id"] = apiKeyId;

        var defaultAgent = args.Bool("default_agent");
        if (defaultAgent.HasValue) payload["default_agent"] = defaultAgent.Value;

        if (payload.Count == 0)
            return """{"error":"No fields to update. Provide 'agent_api_key_id' or 'default_agent'."}""";

        return await _client.UpdateConversationRouteAsync(token, id, JsonSerializer.Serialize(payload), ct);
    }
}
