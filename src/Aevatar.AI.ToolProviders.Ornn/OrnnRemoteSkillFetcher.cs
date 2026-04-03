// ─────────────────────────────────────────────────────────────
// OrnnRemoteSkillFetcher — 从 Ornn 平台拉取技能
// 实现 IRemoteSkillFetcher，将 Ornn API 响应转换为 SkillDefinition
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.ToolProviders.Skills;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>
/// Ornn 远程技能拉取器。通过 OrnnSkillClient 从 Ornn 平台获取技能。
/// </summary>
public sealed class OrnnRemoteSkillFetcher : IRemoteSkillFetcher
{
    private readonly OrnnSkillClient _client;

    public OrnnRemoteSkillFetcher(OrnnSkillClient client) => _client = client;

    public async Task<SkillDefinition?> FetchSkillAsync(
        string accessToken, string nameOrId, CancellationToken ct = default)
    {
        var skill = await _client.GetSkillJsonAsync(accessToken, nameOrId, ct);
        if (skill == null)
            return null;

        // 从 SKILL.md 文件内容中提取 instructions
        var instructions = "";
        Dictionary<string, string>? associatedFiles = null;

        if (skill.Files != null && skill.Files.Count > 0)
        {
            if (skill.Files.TryGetValue("SKILL.md", out var skillMd))
                instructions = skillMd;

            var others = skill.Files
                .Where(f => !f.Key.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Key, f => f.Value);

            if (others.Count > 0)
                associatedFiles = others;
        }

        // 尝试从 instructions 解析 frontmatter
        var parser = new SkillFrontmatterParser();
        var parsed = parser.Parse(instructions);

        return new SkillDefinition
        {
            Name = parsed.Name ?? skill.Name ?? nameOrId,
            Description = parsed.Description ?? skill.Description ?? "",
            Instructions = parsed.Body,
            Source = SkillSource.Remote,
            RemoteId = nameOrId,
            Arguments = parsed.Arguments,
            WhenToUse = parsed.WhenToUse,
            IsModelInvocable = parsed.IsModelInvocable,
            IsUserInvocable = parsed.IsUserInvocable,
            AssociatedFiles = associatedFiles,
        };
    }
}
