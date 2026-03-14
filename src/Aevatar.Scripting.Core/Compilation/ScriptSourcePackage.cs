namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptSourcePackage(
    string Format,
    IReadOnlyList<ScriptSourceFile> CSharpSources,
    IReadOnlyList<ScriptSourceFile> ProtoFiles,
    string EntryBehaviorTypeName)
{
    public const string CurrentFormat = "aevatar.scripting.package.v1";

    public static ScriptSourcePackage Empty { get; } = new(
        CurrentFormat,
        Array.Empty<ScriptSourceFile>(),
        Array.Empty<ScriptSourceFile>(),
        string.Empty);

    public static ScriptSourcePackage SingleSource(string sourceText, string? path = null) =>
        new(
            CurrentFormat,
            [new ScriptSourceFile(path ?? "Behavior.cs", sourceText ?? string.Empty)],
            Array.Empty<ScriptSourceFile>(),
            string.Empty);

    public ScriptSourcePackage Normalize()
    {
        static IReadOnlyList<ScriptSourceFile> NormalizeFiles(IReadOnlyList<ScriptSourceFile> files)
        {
            return files
                .Where(static x => x != null)
                .Select(x => new ScriptSourceFile(
                    ScriptSourceFile.NormalizePath(x.Path),
                    x.Content ?? string.Empty))
                .OrderBy(x => x.NormalizedPath, StringComparer.Ordinal)
                .ToArray();
        }

        return new ScriptSourcePackage(
            string.IsNullOrWhiteSpace(Format) ? CurrentFormat : Format.Trim(),
            NormalizeFiles(CSharpSources ?? Array.Empty<ScriptSourceFile>()),
            NormalizeFiles(ProtoFiles ?? Array.Empty<ScriptSourceFile>()),
            EntryBehaviorTypeName?.Trim() ?? string.Empty);
    }
}
