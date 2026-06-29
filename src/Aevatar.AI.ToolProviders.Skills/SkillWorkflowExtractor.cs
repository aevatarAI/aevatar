namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Workflow extraction result: typed workflow descriptors plus remaining associated files.
/// </summary>
public sealed class SkillWorkflowExtractionResult
{
    public required IReadOnlyList<SkillWorkflowDescriptor> Workflows { get; init; }

    /// <summary>
    /// Associated files after removing workflow files; null when no files remain.
    /// </summary>
    public IReadOnlyDictionary<string, string>? RemainingFiles { get; init; }
}

/// <summary>
/// Extracts workflow templates from the canonical <c>workflows/*.{yaml,yml}</c> package root.
/// </summary>
public sealed class SkillWorkflowExtractor
{
    private const string WorkflowsDir = "workflows";

    /// <summary>
    /// Extracts workflows from an Ornn files dictionary.
    /// </summary>
    public SkillWorkflowExtractionResult ExtractFromFiles(IReadOnlyDictionary<string, string>? files)
    {
        if (files == null || files.Count == 0)
            return new SkillWorkflowExtractionResult { Workflows = [], RemainingFiles = files };

        var workflowCandidates = files
            .Where(kv => HasDirectoryPrefix(kv.Key, WorkflowsDir) && IsYamlFile(kv.Key))
            .ToList();

        var workflows = workflowCandidates
            .Select(kv => TryParse(kv.Value))
            .Where(w => w != null)
            .Select(w => w!)
            .ToList();
        var consumedKeys = workflowCandidates
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

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
    /// Extracts workflows from a local skill directory's canonical <c>workflows/</c> subdirectory.
    /// </summary>
    public IReadOnlyList<SkillWorkflowDescriptor> ExtractFromDirectory(string skillDir)
    {
        if (string.IsNullOrWhiteSpace(skillDir))
            return [];

        var workflowsDir = Path.Combine(skillDir, WorkflowsDir);
        if (Directory.Exists(workflowsDir))
            return EnumerateYaml(workflowsDir, skillDir);

        return [];
    }

    private static IReadOnlyList<SkillWorkflowDescriptor> EnumerateYaml(
        string dir,
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
            var parsed = TryParse(yaml);
            if (parsed != null)
                results.Add(parsed);
        }

        return results;
    }

    private static SkillWorkflowDescriptor? TryParse(string yaml)
    {
        var meta = ReadTopLevelMetadata(yaml);
        if (string.IsNullOrWhiteSpace(meta.Name))
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
            }
        }

        return new TopLevelMetadata(name);
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

    private sealed record TopLevelMetadata(string? Name);
}
