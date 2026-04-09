// ─────────────────────────────────────────────────────────────
// LLMRequest / ChatMessage / ToolCall — LLM 请求与消息模型
// 封装 Chat API 所需的消息列表、工具、参数等
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>LLM 请求 DTO。包含消息、工具、模型参数。</summary>
public sealed class LLMRequest
{
    /// <summary>对话消息列表，按顺序排列（system / user / assistant / tool）。</summary>
    public required List<ChatMessage> Messages { get; init; }

    /// <summary>稳定请求标识，用于 replay/dedup/outbox 等跨边界关联。</summary>
    public string? RequestId { get; init; }

    /// <summary>透传给 provider/middleware 的附加 metadata。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>可选工具列表，供 LLM 选择调用。</summary>
    public IReadOnlyList<IAgentTool>? Tools { get; init; }

    /// <summary>可选模型名称，覆盖 Provider 默认模型。</summary>
    public string? Model { get; init; }

    /// <summary>可选温度参数，控制生成随机性。</summary>
    public double? Temperature { get; init; }

    /// <summary>可选最大生成 Token 数。</summary>
    public int? MaxTokens { get; init; }

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

/// <summary>单条 Chat 消息。支持 system / user / assistant / tool 四种角色。</summary>
public sealed class ChatMessage
{
    /// <summary>消息角色：system / user / assistant / tool。</summary>
    public required string Role { get; init; }

    /// <summary>文本内容，tool 角色时表示工具执行结果。</summary>
    public string? Content { get; init; }

    /// <summary>多模态内容分片（文本/图片）。存在时优先由 Provider 按分片构造消息。</summary>
    public IReadOnlyList<ContentPart>? ContentParts { get; init; }

    /// <summary>tool 角色时，对应 tool_call 的 Id。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>assistant 角色时，LLM 返回的 tool_call 列表。</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>创建 system 角色消息。</summary>
    public static ChatMessage System(string content) => new() { Role = "system", Content = content };

    /// <summary>创建 user 角色消息。</summary>
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };

    /// <summary>创建 assistant 角色消息。</summary>
    public static ChatMessage Assistant(string content) => new() { Role = "assistant", Content = content };

    /// <summary>创建 tool 角色消息，携带工具执行结果。</summary>
    /// <param name="callId">对应 tool_call 的 Id。</param>
    /// <param name="result">工具执行结果 JSON 字符串。</param>
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

/// <summary>多模态内容分片。</summary>
public sealed class ContentPart
{
    /// <summary>分片类型：text / image / audio / video。</summary>
    public required ContentPartKind Kind { get; init; }

    /// <summary>文本分片内容。</summary>
    public string? Text { get; init; }

    /// <summary>媒体分片的内联 base64 数据（不带 data-uri 头）。</summary>
    public string? DataBase64 { get; init; }

    /// <summary>媒体 MIME 类型（例如 image/png、audio/wav、video/mp4）。</summary>
    public string? MediaType { get; init; }

    /// <summary>媒体远程地址或 data-uri。</summary>
    public string? Uri { get; init; }

    /// <summary>可选显示名或文件名。</summary>
    public string? Name { get; init; }

    /// <summary>创建文本分片。</summary>
    public static ContentPart TextPart(string text) =>
        new() { Kind = ContentPartKind.Text, Text = text };

    /// <summary>创建图片分片。</summary>
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

/// <summary>单次工具调用。包含 Id、名称、参数 JSON。</summary>
public sealed class ToolCall
{
    /// <summary>工具调用唯一标识。</summary>
    public required string Id { get; init; }

    /// <summary>工具名称。</summary>
    public required string Name { get; init; }

    /// <summary>工具参数 JSON 字符串。</summary>
    public required string ArgumentsJson { get; init; }
}
