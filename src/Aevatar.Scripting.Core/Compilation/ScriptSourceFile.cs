namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptSourceFile(
    string Path,
    string Content)
{
    public string NormalizedPath => NormalizePath(Path);

    public static string NormalizePath(string? path)
    {
        var normalized = (path ?? string.Empty)
            .Replace('\\', '/')
            .Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return string.IsNullOrWhiteSpace(normalized)
            ? "file"
            : normalized.TrimStart('/');
    }
}
