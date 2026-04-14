// ─────────────────────────────────────────────────────────────
// LLMRequest / ChatMessage / ToolCall — LLM request and message models
// Encapsulates the message list, tools, parameters, and other data required by the Chat API
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>LLM request DTO. Includes messages, tools, and model parameters.</summary>
public sealed class LLMRequest
{
    /// <summary>Conversation message list in order (system / user / assistant / tool).</summary>
    public required List<ChatMessage> Messages { get; init; }

    /// <summary>Stable request identifier used for cross-boundary correlation in replay/dedup/outbox scenarios.</summary>
    public string? RequestId { get; init; }

    /// <summary>Additional metadata passed through to the provider/middleware.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Optional list of tools available for the LLM to invoke.</summary>
    public IReadOnlyList<IAgentTool>? Tools { get; init; }

    /// <summary>Optional model name that overrides the provider default model.</summary>
    public string? Model { get; init; }

    /// <summary>Optional temperature parameter that controls generation randomness.</summary>
    public double? Temperature { get; init; }

    /// <summary>Optional maximum number of output tokens.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Optional response format constraint (Text / JsonObject / JsonSchema).</summary>
    public LLMResponseFormat? ResponseFormat { get; init; }

    public IReadOnlySet<ContentPartKind> GetRequestedInputModalities()
    {
        var modalities = new HashSet<ContentPartKind>();
        foreach (var message in Messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
                modalities.Add(ContentPartKind.Text);

            if (message.ContentParts is not { Count: > 0 })
                continue;

            foreach (var part in message.ContentParts)
            {
                if (part == null || part.Kind == ContentPartKind.Unspecified)
                    continue;

                modalities.Add(part.Kind);
            }
        }

        return modalities;
    }
}

/// <summary>A single Chat message. Supports the system / user / assistant / tool roles.</summary>
public sealed class ChatMessage
{
    /// <summary>Message role: system / user / assistant / tool.</summary>
    public required string Role { get; init; }

    /// <summary>Text content; for the tool role, this represents the tool execution result.</summary>
    public string? Content { get; init; }

    /// <summary>Multimodal content parts (text/image). When present, the provider should construct the message from the parts first.</summary>
    public IReadOnlyList<ContentPart>? ContentParts { get; init; }

    /// <summary>For the tool role, the corresponding tool_call Id.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For the assistant role, the tool_call list returned by the LLM.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Creates a system-role message.</summary>
    public static ChatMessage System(string content) => new() { Role = "system", Content = content };

    /// <summary>Creates a user-role message.</summary>
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };

    /// <summary>Creates an assistant-role message.</summary>
    public static ChatMessage Assistant(string content) => new() { Role = "assistant", Content = content };

    /// <summary>Creates a tool-role message carrying the tool execution result.</summary>
    /// <param name="callId">The corresponding tool_call Id.</param>
    /// <param name="result">Tool execution result as a JSON string.</param>
    public static ChatMessage Tool(string callId, string result) => new() { Role = "tool", ToolCallId = callId, Content = result };

    public static ChatMessage User(IReadOnlyList<ContentPart> parts, string? text = null) => new()
    {
        Role = "user",
        Content = text,
        ContentParts = parts,
    };
}

public enum ContentPartKind
{
    Unspecified = 0,
    Text = 1,
    Image = 2,
    Audio = 3,
    Video = 4,
}

/// <summary>Multimodal content part.</summary>
public sealed class ContentPart
{
    /// <summary>Part kind: text / image / audio / video.</summary>
    public required ContentPartKind Kind { get; init; }

    /// <summary>Text part content.</summary>
    public string? Text { get; init; }

    /// <summary>Inline base64 data for a media part (without the data-uri prefix).</summary>
    public string? DataBase64 { get; init; }

    /// <summary>Media MIME type (for example image/png, audio/wav, video/mp4).</summary>
    public string? MediaType { get; init; }

    /// <summary>Remote media URI or data-uri.</summary>
    public string? Uri { get; init; }

    /// <summary>Optional display name or file name.</summary>
    public string? Name { get; init; }

    /// <summary>Creates a text part.</summary>
    public static ContentPart TextPart(string text) =>
        new() { Kind = ContentPartKind.Text, Text = text };

    /// <summary>Creates an image part.</summary>
    public static ContentPart ImagePart(string dataBase64, string mediaType = "image/png", string? name = null) =>
        new() { Kind = ContentPartKind.Image, DataBase64 = dataBase64, MediaType = mediaType, Name = name };

    public static ContentPart ImageUriPart(string uri, string mediaType = "image/png", string? name = null) =>
        new() { Kind = ContentPartKind.Image, Uri = uri, MediaType = mediaType, Name = name };

    public static ContentPart AudioPart(string dataBase64, string mediaType = "audio/wav", string? name = null) =>
        new() { Kind = ContentPartKind.Audio, DataBase64 = dataBase64, MediaType = mediaType, Name = name };

    public static ContentPart AudioUriPart(string uri, string mediaType = "audio/wav", string? name = null) =>
        new() { Kind = ContentPartKind.Audio, Uri = uri, MediaType = mediaType, Name = name };

    public static ContentPart VideoPart(string dataBase64, string mediaType = "video/mp4", string? name = null) =>
        new() { Kind = ContentPartKind.Video, DataBase64 = dataBase64, MediaType = mediaType, Name = name };

    public static ContentPart VideoUriPart(string uri, string mediaType = "video/mp4", string? name = null) =>
        new() { Kind = ContentPartKind.Video, Uri = uri, MediaType = mediaType, Name = name };
}

/// <summary>A single tool call. Includes Id, name, and parameter JSON.</summary>
public sealed class ToolCall
{
    /// <summary>Unique tool call identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Tool parameter JSON string.</summary>
    public required string ArgumentsJson { get; init; }
}
