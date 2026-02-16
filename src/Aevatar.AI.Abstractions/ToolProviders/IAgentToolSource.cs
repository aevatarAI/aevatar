// ─────────────────────────────────────────────────────────────
// IAgentToolSource — Agent 工具来源接口
// 用于从外部系统（MCP/Skills/内置）发现可注册工具
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Agent 工具来源。负责按需发现并返回工具列表。
/// </summary>
public interface IAgentToolSource
{
    /// <summary>
    /// 发现工具。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用工具集合。</returns>
    Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default);
}
