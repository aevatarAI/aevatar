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
        return string.IsNullOrWhiteSpace(normalized)
            ? "file"
            : normalized.TrimStart('/');
    }
}
