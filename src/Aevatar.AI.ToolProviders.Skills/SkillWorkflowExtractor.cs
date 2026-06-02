// ─────────────────────────────────────────────────────────────
// SkillWorkflowExtractor — 从技能包中识别工作流 YAML
// 优先 workflows/，否则在 assets/ 中按 workflow 结构（name + steps）挑出
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 工作流抽取结果：识别出的工作流 + 去除工作流文件后的剩余关联文件。
/// </summary>
public sealed class SkillWorkflowExtractionResult
{
    public required IReadOnlyList<SkillWorkflowDescriptor> Workflows { get; init; }

    /// <summary>
    /// 去除工作流文件之后的关联文件；若无剩余则为 null。
    /// </summary>
    public IReadOnlyDictionary<string, string>? RemainingFiles { get; init; }
}

/// <summary>
/// 从 skill 包中识别可执行工作流。
/// 优先扫描 <c>workflows/*.{yaml,yml}</c>；若该目录缺席，回退到 <c>assets/*.{yaml,yml}</c>，
/// 仅当 YAML 顶层同时含 <c>name</c> 与 <c>steps</c> 时才视为工作流。
/// </summary>
public sealed class SkillWorkflowExtractor
{
    private const string WorkflowsDir = "workflows";
    private const string AssetsDir = "assets";

    /// <summary>
    /// 从 ornn 拉取得到的 files 字典中抽取工作流。
    /// </summary>
    public SkillWorkflowExtractionResult ExtractFromFiles(IReadOnlyDictionary<string, string>? files)
    {
        if (files == null || files.Count == 0)
            return new SkillWorkflowExtractionResult { Workflows = [], RemainingFiles = files };

        var workflowsCandidates = files
            .Where(kv => HasDirectoryPrefix(kv.Key, WorkflowsDir) && IsYamlFile(kv.Key))
            .ToList();

        List<SkillWorkflowDescriptor> workflows;
        HashSet<string> consumedKeys;

        if (workflowsCandidates.Count > 0)
        {
            workflows = workflowsCandidates
                .Select(kv => TryParse(kv.Key, kv.Value, requireWorkflowShape: false))
                .Where(w => w != null)
                .Select(w => w!)
                .ToList();
            consumedKeys = workflowsCandidates
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.Ordinal);
        }
        else
        {
            var assetsCandidates = files
                .Where(kv => HasDirectoryPrefix(kv.Key, AssetsDir) && IsYamlFile(kv.Key))
                .ToList();

            workflows = [];
            consumedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in assetsCandidates)
            {
                var parsed = TryParse(kv.Key, kv.Value, requireWorkflowShape: true);
                if (parsed == null)
                    continue;
                workflows.Add(parsed);
                consumedKeys.Add(kv.Key);
            }
        }

        IReadOnlyDictionary<string, string>? remaining = null;
        if (consumedKeys.Count < files.Count)
        {
            remaining = files
                .Where(kv => !consumedKeys.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (remaining.Count == 0)
                remaining = null;
        }

        return new SkillWorkflowExtractionResult
        {
            Workflows = workflows,
            RemainingFiles = remaining,
        };
    }

    /// <summary>
    /// 扫描本地 skill 目录，从 <c>workflows/</c>（缺席则 <c>assets/</c>）中抽取工作流。
    /// </summary>
    public IReadOnlyList<SkillWorkflowDescriptor> ExtractFromDirectory(string skillDir)
    {
        if (string.IsNullOrWhiteSpace(skillDir))
            return [];

        var workflowsDir = Path.Combine(skillDir, WorkflowsDir);
        if (Directory.Exists(workflowsDir))
            return EnumerateYaml(workflowsDir, requireWorkflowShape: false, skillDir);

        var assetsDir = Path.Combine(skillDir, AssetsDir);
        if (Directory.Exists(assetsDir))
            return EnumerateYaml(assetsDir, requireWorkflowShape: true, skillDir);

        return [];
    }

    private static IReadOnlyList<SkillWorkflowDescriptor> EnumerateYaml(
        string dir,
        bool requireWorkflowShape,
        string skillRoot)
    {
        var results = new List<SkillWorkflowDescriptor>();
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            if (!IsYamlFile(path))
                continue;
            string yaml;
            try
            {
                yaml = File.ReadAllText(path);
            }
            catch
            {
                continue;
            }

            var fileName = Path.GetRelativePath(skillRoot, path).Replace('\\', '/');
            var parsed = TryParse(fileName, yaml, requireWorkflowShape);
            if (parsed != null)
                results.Add(parsed);
        }

        return results;
    }

    private static SkillWorkflowDescriptor? TryParse(string fileName, string yaml, bool requireWorkflowShape)
    {
        var meta = ReadTopLevelMetadata(yaml);
        if (string.IsNullOrWhiteSpace(meta.Name))
            return null;
        if (requireWorkflowShape && !meta.HasSteps)
            return null;

        return new SkillWorkflowDescriptor
        {
            WorkflowId = meta.Name!,
            WorkflowYamls = [yaml.Trim()],
        };
    }

    private static TopLevelMetadata ReadTopLevelMetadata(string yaml)
    {
        string? name = null;
        string? description = null;
        string? whenToUse = null;
        var hasSteps = false;

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var first = line[0];
            // Only top-level keys (column 0, not list item, not comment).
            if (char.IsWhiteSpace(first) || first == '#' || first == '-')
                continue;

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
                continue;

            var key = line[..colonIndex].Trim().ToLowerInvariant();
            var value = StripInlineComment(line[(colonIndex + 1)..]).Trim();
            value = StripWrappingQuotes(value);

            switch (key)
            {
                case "name":
                    name ??= value;
                    break;
                case "description":
                    description ??= value;
                    break;
                case "when_to_use" or "when-to-use":
                    whenToUse ??= value;
                    break;
                case "steps":
                    hasSteps = true;
                    break;
            }
        }

        return new TopLevelMetadata(name, description, whenToUse, hasSteps);
    }

    private static bool HasDirectoryPrefix(string key, string dirName)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        // Accept both "workflows/foo.yaml" and "workflows\foo.yaml" for safety.
        var prefixLen = dirName.Length;
        if (key.Length <= prefixLen)
            return false;
        if (!key.AsSpan(0, prefixLen).Equals(dirName, StringComparison.OrdinalIgnoreCase))
            return false;
        var separator = key[prefixLen];
        return separator == '/' || separator == '\\';
    }

    private static bool IsYamlFile(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    private static string StripInlineComment(string value)
    {
        var hashIndex = value.IndexOf('#');
        return hashIndex < 0 ? value : value[..hashIndex];
    }

    private static string StripWrappingQuotes(string value)
    {
        if (value.Length < 2)
            return value;
        var first = value[0];
        var last = value[^1];
        if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            return value[1..^1];
        return value;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record TopLevelMetadata(string? Name, string? Description, string? WhenToUse, bool HasSteps);
}
