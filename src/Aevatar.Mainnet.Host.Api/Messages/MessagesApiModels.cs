using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.Mainnet.Host.Api.Messages;

// ---- Anthropic Messages wire DTO --------------------------------------------------
//
// Surface mirrors anthropic.com/claude/reference/messages_post. Path B is a
// stateless facade: every POST /v1/messages opens + closes its own LlmSession,
// no previous_response_id equivalent (Messages protocol has no native one).

internal sealed record MessagesCreateRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("system")]
    public JsonElement System { get; init; }

    [JsonPropertyName("messages")]
    public JsonElement Messages { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; init; }

    [JsonPropertyName("stop_sequences")]
    public IReadOnlyList<string>? StopSequences { get; init; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }

    [JsonPropertyName("tools")]
    public JsonElement Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public JsonElement ToolChoice { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; init; }
}
