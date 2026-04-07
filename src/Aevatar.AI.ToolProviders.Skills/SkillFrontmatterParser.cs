// ─────────────────────────────────────────────────────────────
// SkillFrontmatterParser — SKILL.md Frontmatter 解析器
// 解析 --- 分隔的 YAML frontmatter，提取元数据和正文指令
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// SKILL.md frontmatter 解析结果。
/// </summary>
public sealed class SkillParseResult
{
    /// <summary>Frontmatter 中的 name 字段。</summary>
    public string? Name { get; init; }

    /// <summary>Frontmatter 中的 description 字段。</summary>
    public string? Description { get; init; }

    /// <summary>Frontmatter 中的 arguments 字段。</summary>
    public string? Arguments { get; init; }

    /// <summary>Frontmatter 中的 when-to-use 字段。</summary>
    public string? WhenToUse { get; init; }

    /// <summary>是否允许 LLM 调用（disable-model-invocation 的反义）。</summary>
    public bool IsModelInvocable { get; init; } = true;

    /// <summary>是否允许用户手动调用。</summary>
    public bool IsUserInvocable { get; init; } = true;

    /// <summary>Frontmatter 之后的正文内容（Instructions）。</summary>
    public required string Body { get; init; }

    /// <summary>是否存在 frontmatter。</summary>
    public bool HasFrontmatter { get; init; }
}

/// <summary>
/// 解析 SKILL.md 格式的 frontmatter。
/// 支持 --- 分隔的 YAML frontmatter，也兼容无 frontmatter 的旧格式。
/// </summary>
public sealed class SkillFrontmatterParser
{
    /// <summary>
    /// 解析 SKILL.md 内容。
    /// 如果以 --- 开头，提取 frontmatter；否则回退到 H1 + 首段格式。
    /// </summary>
    public SkillParseResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new SkillParseResult { Body = "" };

        var trimmed = content.TrimStart();

        // 检测 frontmatter 开始标记
        if (trimmed.StartsWith("---"))
            return ParseWithFrontmatter(trimmed);

        return ParseLegacy(content);
    }

    private static SkillParseResult ParseWithFrontmatter(string content)
    {
        // 跳过第一个 ---
        var afterFirst = content.IndexOf('\n');
        if (afterFirst < 0)
            return new SkillParseResult { Body = content, HasFrontmatter = false };

        var rest = content[(afterFirst + 1)..];
        var closingIndex = rest.IndexOf("\n---", StringComparison.Ordinal);
        if (closingIndex < 0)
        {
            // 没有关闭标记，视为无 frontmatter
            return ParseLegacy(content);
        }

        var frontmatterBlock = rest[..closingIndex];
        var bodyStart = closingIndex + 4; // "\n---".Length
        // 跳过 --- 后的可能换行
        if (bodyStart < rest.Length && rest[bodyStart] == '\n')
            bodyStart++;

        var body = bodyStart < rest.Length ? rest[bodyStart..].TrimStart('\n') : "";

        // 解析 frontmatter 键值对
        string? name = null, description = null, arguments = null, whenToUse = null;
        var isModelInvocable = true;
        var isUserInvocable = true;

        foreach (var line in frontmatterBlock.Split('\n'))
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith('#'))
                continue;

            var colonIndex = trimmedLine.IndexOf(':');
            if (colonIndex <= 0)
                continue;

            var key = trimmedLine[..colonIndex].Trim().ToLowerInvariant();
            var value = trimmedLine[(colonIndex + 1)..].Trim();

            // 去除首尾引号
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            switch (key)
            {
                case "name":
                    name = value;
                    break;
                case "description":
                    description = value;
                    break;
                case "arguments":
                    arguments = value;
                    break;
                case "when-to-use" or "when_to_use":
                    whenToUse = value;
                    break;
                case "disable-model-invocation" or "disable_model_invocation":
                    isModelInvocable = !ParseBool(value);
                    break;
                case "user-invocable" or "user_invocable":
                    isUserInvocable = ParseBool(value);
                    break;
            }
        }

        return new SkillParseResult
        {
            Name = name,
            Description = description,
            Arguments = arguments,
            WhenToUse = whenToUse,
            IsModelInvocable = isModelInvocable,
            IsUserInvocable = isUserInvocable,
            Body = body,
            HasFrontmatter = true,
        };
    }

    /// <summary>
    /// 旧格式解析：第一行 # 标题作为名称，第一段作为描述，其余为指令。
    /// </summary>
    private static SkillParseResult ParseLegacy(string content)
    {
        var lines = content.Split('\n');
        var name = lines[0].TrimStart('#', ' ').Trim();

        var description = "";
        var instructionsStart = 1;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) && !string.IsNullOrEmpty(description))
            {
                instructionsStart = i + 1;
                break;
            }

            if (!string.IsNullOrEmpty(line))
                description += (description.Length > 0 ? " " : "") + line;
        }

        var body = string.Join("\n", lines.Skip(instructionsStart));

        return new SkillParseResult
        {
            Name = string.IsNullOrEmpty(name) ? null : name,
            Description = string.IsNullOrEmpty(description) ? null : description,
            Body = body,
            HasFrontmatter = false,
        };
    }

    private static bool ParseBool(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value == "1" ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
