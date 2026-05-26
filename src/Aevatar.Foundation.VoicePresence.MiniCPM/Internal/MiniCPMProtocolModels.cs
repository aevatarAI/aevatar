using System.Text.Json.Serialization;

namespace Aevatar.Foundation.VoicePresence.MiniCPM.Internal;

internal sealed class MiniCPMMessageRequest
{
    [JsonPropertyName("messages")]
    public List<MiniCPMMessage> Messages { get; init; } = [];
}

internal sealed class MiniCPMMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("content")]
    public List<MiniCPMMessageContent> Content { get; init; } = [];
}

internal sealed class MiniCPMMessageContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("input_audio")]
    public MiniCPMInputAudio? InputAudio { get; init; }
}

internal sealed class MiniCPMInputAudio
{
    [JsonPropertyName("data")]
    public string Data { get; init; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; init; } = "wav";
}

internal sealed class MiniCPMCompletionsFrame
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("response_id")]
    public int? ResponseId { get; init; }

    [JsonPropertyName("choices")]
    public List<MiniCPMCompletionsChoice>? Choices { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed class MiniCPMCompletionsChoice
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("audio")]
    public string? Audio { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}
