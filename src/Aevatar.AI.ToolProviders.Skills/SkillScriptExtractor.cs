namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// C# script extraction result plus files that should remain generic attachments.
/// </summary>
public sealed class SkillScriptExtractionResult
{
    public required IReadOnlyList<SkillScriptDescriptor> Scripts { get; init; }

    /// <summary>
    /// Files not consumed as script source or protobuf input; null when no files remain.
    /// </summary>
    public IReadOnlyDictionary<string, string>? RemainingFiles { get; init; }
}

/// <summary>
/// Extracts typed C# script assets from skill packages.
/// </summary>
public sealed class SkillScriptExtractor
{
    private const string ScriptsDir = "scripts";
    private const string AssetsDir = "assets";
    private static readonly char[] PathSeparators = ['/', '\\'];

    public SkillScriptExtractionResult ExtractFromFiles(
        string skillName,
        string? scriptEntry,
        IReadOnlyDictionary<string, string>? files)
    {
        if (files == null || files.Count == 0)
            return new SkillScriptExtractionResult { Scripts = [], RemainingFiles = files };

        var scriptCandidates = GetCandidateScriptFiles(files, ScriptsDir);
        var protoBoundary = ScriptsDir;
        if (scriptCandidates.Count == 0)
        {
            scriptCandidates = GetCandidateScriptFiles(files, AssetsDir);
            protoBoundary = AssetsDir;
        }

        if (scriptCandidates.Count == 0)
            return new SkillScriptExtractionResult { Scripts = [], RemainingFiles = files };

        var protoFiles = files
            .Where(kv => IsDirectChildOf(kv.Key, protoBoundary) && IsProtoFile(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => NormalizePath(kv.Key), kv => kv.Value.Trim(), StringComparer.Ordinal);

        var sourceFiles = scriptCandidates
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => NormalizePath(kv.Key), kv => kv.Value, StringComparer.Ordinal);

        var consumedKeys = scriptCandidates
            .Select(kv => kv.Key)
            .Concat(files
                .Where(kv => IsDirectChildOf(kv.Key, protoBoundary) && IsProtoFile(kv.Key))
                .Select(kv => kv.Key))
            .ToHashSet(StringComparer.Ordinal);

        return new SkillScriptExtractionResult
        {
            Scripts =
            [
                new SkillScriptDescriptor
                {
                    ScriptId = BuildScriptId(skillName, sourceFiles.Keys.First()),
                    SourceFiles = sourceFiles,
                    ProtoFiles = protoFiles,
                    EntryBehaviorTypeName = scriptEntry?.Trim() ?? string.Empty,
                }
            ],
            RemainingFiles = BuildRemainingFiles(files, consumedKeys),
        };
    }

    public IReadOnlyList<SkillScriptDescriptor> ExtractFromDirectory(
        string skillDir,
        string skillName,
        string? scriptEntry)
    {
        if (string.IsNullOrWhiteSpace(skillDir) || !Directory.Exists(skillDir))
            return [];

        var extraction = ExtractFromFiles(skillName, scriptEntry, ReadDirectoryFiles(skillDir, ScriptsDir));
        if (extraction.Scripts.Count > 0)
            return extraction.Scripts;

        return ExtractFromFiles(skillName, scriptEntry, ReadDirectoryFiles(skillDir, AssetsDir)).Scripts;
    }

    private static List<KeyValuePair<string, string>> GetCandidateScriptFiles(
        IReadOnlyDictionary<string, string> files,
        string dirName)
    {
        return files
            .Where(kv => IsDirectChildOf(kv.Key, dirName) &&
                         IsCSharpFile(kv.Key) &&
                         !string.IsNullOrWhiteSpace(kv.Value))
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> ReadDirectoryFiles(string skillDir, string dirName)
    {
        var dir = Path.Combine(skillDir, dirName);
        if (!Directory.Exists(dir))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            if (!IsCSharpFile(path) && !IsProtoFile(path))
                continue;

            var relativePath = Path.GetRelativePath(skillDir, path).Replace('\\', '/');
            try
            {
                files[relativePath] = File.ReadAllText(path);
            }
            catch
            {
                // Local discovery should ignore unreadable optional assets.
            }
        }

        return files;
    }

    private static IReadOnlyDictionary<string, string>? BuildRemainingFiles(
        IReadOnlyDictionary<string, string> files,
        HashSet<string> consumedKeys)
    {
        if (consumedKeys.Count >= files.Count)
            return null;

        var remaining = files
            .Where(kv => !consumedKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return remaining.Count == 0 ? null : remaining;
    }

    private static string BuildScriptId(string skillName, string entryFilePath)
    {
        var normalizedSkill = Slugify(skillName);
        var entryFileId = Slugify(Path.GetFileNameWithoutExtension(
            entryFilePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? entryFilePath));

        if (string.IsNullOrEmpty(normalizedSkill))
            normalizedSkill = "skill";
        if (string.IsNullOrEmpty(entryFileId))
            entryFileId = "script";

        return $"{normalizedSkill}-{entryFileId}";
    }

    private static string Slugify(string value)
    {
        var chars = new List<char>(value.Length);
        var previousDash = false;

        foreach (var c in value.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(char.ToLowerInvariant(c));
                previousDash = false;
            }
            else if (!previousDash && chars.Count > 0)
            {
                chars.Add('-');
                previousDash = true;
            }
        }

        if (chars.Count > 0 && chars[^1] == '-')
            chars.RemoveAt(chars.Count - 1);

        return new string(chars.ToArray());
    }

    private static bool IsDirectChildOf(string key, string dirName)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        var normalized = NormalizePath(key);
        var prefixLen = dirName.Length;
        if (normalized.Length <= prefixLen)
            return false;
        if (!normalized.AsSpan(0, prefixLen).Equals(dirName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (normalized[prefixLen] != '/')
            return false;

        return !normalized[(prefixLen + 1)..].Contains('/');
    }

    private static bool IsCSharpFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtoFile(string path) =>
        path.EndsWith(".proto", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
