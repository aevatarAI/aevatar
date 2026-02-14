// ─────────────────────────────────────────────────────────────
// IAgentTool — Agent 工具接口
// 定义工具契约：名称、描述、参数 Schema、执行方法
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>Agent 可调用工具接口。LLM 通过 tool_call 触发执行。</summary>
public interface IAgentTool
{
    /// <summary>工具名称，用于 LLM 选择与调用。</summary>
    string Name { get; }

    /// <summary>工具描述，供 LLM 理解用途。</summary>
    string Description { get; }

    /// <summary>工具参数 JSON Schema，描述输入格式。</summary>
    string ParametersSchema { get; }

    /// <summary>执行工具。参数为 JSON 字符串，返回结果为 JSON 字符串。</summary>
    /// <param name="argumentsJson">LLM 传入的参数 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>工具执行结果，通常为 JSON 字符串。</returns>
    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}
