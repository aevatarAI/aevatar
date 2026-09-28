using System.Security.Cryptography;
using System.Text;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Computes content-safe diagnostics for a skill payload without retaining or logging file bodies.
/// The digest is based on normalized paths, UTF-8 lengths, and per-file content hashes.
/// </summary>
public static class SkillPayloadDiagnostics
{
    public static SkillPayloadDiagnosticSummary Summarize(
        IReadOnlyDictionary<string, string>? files) =>
        Summarize(files is null
            ? []
            : files.Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));

    public static SkillPayloadDiagnosticSummary Summarize(
        IEnumerable<KeyValuePair<string, string>> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var entries = files
            .Select(static pair =>
            {
                var path = NormalizePath(pair.Key);
                var content = pair.Value ?? string.Empty;
                var bytes = Encoding.UTF8.GetByteCount(content);
                var contentHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                    .ToLowerInvariant();
                return new FileEntry(path, bytes, contentHash);
            })
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(static entry => entry.ContentHash, StringComparer.Ordinal)
            .ToArray();

        var canonical = new StringBuilder();
        foreach (var entry in entries)
        {
            canonical.Append(entry.Path)
                .Append('\0')
                .Append(entry.Utf8Bytes)
                .Append('\0')
                .Append(entry.ContentHash)
                .Append('\n');
        }

        var largest = entries
            .OrderByDescending(static entry => entry.Utf8Bytes)
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        var root = entries
            .Where(static entry =>
                string.Equals(entry.Path, "SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static entry => entry.Utf8Bytes)
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
            .FirstOrDefault();

        return new SkillPayloadDiagnosticSummary
        {
            FileCount = entries.Length,
            EmptyFileCount = entries.Count(static entry => entry.Utf8Bytes == 0),
            TotalFileBytes = entries.Sum(static entry => (long)entry.Utf8Bytes),
            RootSkillBytes = root?.Utf8Bytes ?? 0,
            LargestFileBytes = largest?.Utf8Bytes ?? 0,
            LargestFilePath = largest?.Path ?? string.Empty,
            FileTreeSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
                .ToLowerInvariant(),
        };
    }

    private static string NormalizePath(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    private sealed record FileEntry(
        string Path,
        int Utf8Bytes,
        string ContentHash);
}

public sealed record SkillPayloadDiagnosticSummary
{
    public int FileCount { get; init; }

    public int EmptyFileCount { get; init; }

    public long TotalFileBytes { get; init; }

    public int RootSkillBytes { get; init; }

    public int LargestFileBytes { get; init; }

    public string LargestFilePath { get; init; } = string.Empty;

    public string FileTreeSha256 { get; init; } = string.Empty;
}
