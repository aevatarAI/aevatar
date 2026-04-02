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

    /// <summary>工具审批模式。默认 NeverRequire（立即执行）。</summary>
    ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    /// <summary>工具是否只读（不修改外部状态）。Auto 模式下只读工具自动放行。</summary>
    bool IsReadOnly => false;

    /// <summary>工具是否有破坏性（删除、覆写等不可逆操作）。Auto 模式下破坏性工具要求审批。</summary>
    bool IsDestructive => false;

    /// <summary>
    /// Runtime approval check: given the actual call arguments, does this specific
    /// invocation require approval? Returns null to fall back to the static
    /// <see cref="IsReadOnly"/>/<see cref="IsDestructive"/> classification.
    /// Override this for tools where destructiveness depends on call-time arguments
    /// (e.g., HTTP proxy: GET is read-only, POST is destructive).
    /// </summary>
    bool? RequiresApproval(string argumentsJson) => null;

    /// <summary>执行工具。参数为 JSON 字符串，返回结果为 JSON 字符串。</summary>
    /// <param name="argumentsJson">LLM 传入的参数 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>工具执行结果，通常为 JSON 字符串。</returns>
    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}
