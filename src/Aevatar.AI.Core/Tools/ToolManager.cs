// ─────────────────────────────────────────────────────────────
// ToolManager — 工具注册与执行管理器
// 按名称注册/获取工具，执行 tool_call 并返回 ChatMessage
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

/// <summary>工具管理器。负责注册、查找、执行 IAgentTool。</summary>
public sealed class ToolManager
{
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册单个工具。同名覆盖。</summary>
    /// <param name="tool">要注册的工具。</param>
    public void Register(IAgentTool tool) => _tools[tool.Name] = tool;

    /// <summary>批量注册工具。</summary>
    /// <param name="tools">要注册的工具集合。</param>
    public void Register(IEnumerable<IAgentTool> tools) { foreach (var t in tools) _tools[t.Name] = t; }

    /// <summary>清空已注册工具。</summary>
    public void Clear() => _tools.Clear();

    /// <summary>按名称获取工具。未找到返回 null。</summary>
    /// <param name="name">工具名称。</param>
    /// <returns>对应的 IAgentTool，或 null。</returns>
    public IAgentTool? Get(string name) => _tools.GetValueOrDefault(name);

    /// <summary>获取所有已注册工具。</summary>
    /// <returns>工具只读列表。</returns>
    public IReadOnlyList<IAgentTool> GetAll() => _tools.Values.ToList();

    /// <summary>是否已注册至少一个工具。</summary>
    public bool HasTools => _tools.Count > 0;

    /// <summary>执行一次 tool_call，返回 ChatMessage（tool 角色，携带结果或错误）。</summary>
    /// <param name="call">工具调用信息。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>包含执行结果的 ChatMessage，异常时返回 error JSON。</returns>
    public async Task<ChatMessage> ExecuteToolCallAsync(ToolCall call, CancellationToken ct = default)
    {
        var tool = Get(call.Name);
        if (tool == null) return ChatMessage.Tool(call.Id, $"{{\"error\":\"Tool '{call.Name}' not found\"}}");
        try { return ChatMessage.Tool(call.Id, await tool.ExecuteAsync(call.ArgumentsJson, ct)); }
        catch (Exception ex) { return ChatMessage.Tool(call.Id, $"{{\"error\":\"{ex.Message}\"}}"); }
    }
}
