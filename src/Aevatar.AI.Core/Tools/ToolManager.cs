// ─────────────────────────────────────────────────────────────
// ToolManager — 工具注册与执行管理器
// 按名称注册/获取工具，执行 tool_call 并返回 ChatMessage
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
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

    /// <summary>按名称注销工具。不存在时返回 false。</summary>
    /// <param name="name">工具名称。</param>
    public bool Unregister(string name) => _tools.Remove(name);

    /// <summary>按名称获取工具。未找到返回 null。</summary>
    /// <param name="name">工具名称。</param>
    /// <returns>对应的 IAgentTool，或 null。</returns>
    public IAgentTool? Get(string name) => _tools.GetValueOrDefault(name);

    /// <summary>获取所有已注册工具。</summary>
    /// <returns>工具只读列表。</returns>
    public IReadOnlyList<IAgentTool> GetAll() => _tools.Values.ToList();

    /// <summary>是否已注册至少一个工具。</summary>
    public bool HasTools => _tools.Count > 0;

    /// <summary>Build a properly JSON-escaped error object.</summary>
    internal static string BuildErrorJson(string message)
    {
        return JsonSerializer.Serialize(new { error = message });
    }
}
