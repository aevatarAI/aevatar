// ─────────────────────────────────────────────────────────────
// LLMRequest / ChatMessage / ToolCall — LLM 请求与消息模型
// 封装 Chat API 所需的消息列表、工具、参数等
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Tools;

namespace Aevatar.AI.LLM;

/// <summary>LLM 请求 DTO。包含消息、工具、模型参数。</summary>
public sealed class LLMRequest
{
    /// <summary>对话消息列表，按顺序排列（system / user / assistant / tool）。</summary>
    public required List<ChatMessage> Messages { get; init; }

    /// <summary>可选工具列表，供 LLM 选择调用。</summary>
    public IReadOnlyList<IAgentTool>? Tools { get; init; }

    /// <summary>可选模型名称，覆盖 Provider 默认模型。</summary>
    public string? Model { get; init; }

    /// <summary>可选温度参数，控制生成随机性。</summary>
    public double? Temperature { get; init; }

    /// <summary>可选最大生成 Token 数。</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>单条 Chat 消息。支持 system / user / assistant / tool 四种角色。</summary>
public sealed class ChatMessage
{
    /// <summary>消息角色：system / user / assistant / tool。</summary>
    public required string Role { get; init; }

    /// <summary>文本内容，tool 角色时表示工具执行结果。</summary>
    public string? Content { get; init; }

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
