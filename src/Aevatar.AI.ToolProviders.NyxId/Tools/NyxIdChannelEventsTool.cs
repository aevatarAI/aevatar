using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdChannelEventsTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdChannelEventsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_channel_events";

    public string Description =>
        "Push device or analyzer events through the NyxID HTTP Event Gateway. " +
        "Events are forwarded to the agent bound to the target conversation. " +
        "Requires a conversation ID and event metadata (source, type). " +
        "Payload is optional JSON.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "conversation_id": {
              "type": "string",
              "description": "Target conversation ID (required)"
            },
            "source": {
              "type": "string",
              "description": "Logical source of the event, e.g. 'camera-analyzer' (required)"
            },
            "event_type": {
              "type": "string",
              "description": "Event type, e.g. 'person_detected' (required)"
            },
            "event_id": {
              "type": "string",
              "description": "Event ID (UUID, auto-generated if omitted)"
            },
            "timestamp": {
              "type": "string",
              "description": "Event timestamp (RFC 3339, defaults to now)"
            },
            "payload": {
              "type": "object",
              "description": "Optional event payload JSON"
            },
            "metadata": {
              "type": "object",
              "description": "Optional event metadata JSON"
            }
          },
          "required": ["conversation_id", "source", "event_type"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        var conversationId = args.Str("conversation_id");
        var source = args.Str("source");
        var eventType = args.Str("event_type");

        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(eventType))
            return """{"error":"'conversation_id', 'source', and 'event_type' are required"}""";

        var envelope = new Dictionary<string, object?>
        {
            ["event_id"] = args.Str("event_id") ?? Guid.NewGuid().ToString(),
            ["source"] = source,
            ["type"] = eventType,
            ["timestamp"] = args.Str("timestamp") ?? DateTime.UtcNow.ToString("o"),
        };

        var payload = args.Element("payload");
        if (payload.HasValue) envelope["payload"] = payload.Value;

        var metadata = args.Element("metadata");
        if (metadata.HasValue) envelope["metadata"] = metadata.Value;

        return await _client.PushChannelEventAsync(token, conversationId, JsonSerializer.Serialize(envelope), ct);
    }
}
