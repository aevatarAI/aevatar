// ─────────────────────────────────────────────────────────────
// IRemoteSkillFetcher — 远程技能拉取抽象
// 允许 UseSkillTool 按需从远程平台获取技能，不依赖具体实现
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 远程技能拉取器。由具体平台（如 Ornn）实现。
/// </summary>
public interface IRemoteSkillFetcher
{
    /// <summary>
    /// 按名称或 ID 从远程平台拉取技能定义。
    /// </summary>
    /// <param name="accessToken">用户认证令牌。</param>
    /// <param name="nameOrId">技能名称或 ID。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>技能定义，未找到时返回 null。</returns>
    Task<SkillDefinition?> FetchSkillAsync(string accessToken, string nameOrId, CancellationToken ct = default);
}
