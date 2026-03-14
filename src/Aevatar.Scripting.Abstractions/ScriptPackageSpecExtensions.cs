using Google.Protobuf;

namespace Aevatar.Scripting.Abstractions;

public static class ScriptPackageSpecExtensions
{
    public static ScriptPackageSpec CreateSingleSource(
        string source,
        string path = "Behavior.cs",
        string? entryBehaviorTypeName = null)
    {
        var package = new ScriptPackageSpec
        {
            EntryBehaviorTypeName = entryBehaviorTypeName ?? string.Empty,
            EntrySourcePath = path ?? "Behavior.cs",
        };
        package.CsharpSources.Add(new ScriptPackageFile
        {
            Path = string.IsNullOrWhiteSpace(path) ? "Behavior.cs" : path,
            Content = source ?? string.Empty,
        });
        return package;
    }

    public static string GetPrimaryCSharpSource(this ScriptPackageSpec? package)
    {
        if (package == null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(package.EntrySourcePath))
        {
            var match = package.CsharpSources.FirstOrDefault(x =>
                string.Equals(x.Path, package.EntrySourcePath, StringComparison.Ordinal));
            if (match != null)
                return match.Content ?? string.Empty;
        }

        return package.CsharpSources.FirstOrDefault()?.Content ?? string.Empty;
    }

    public static IEnumerable<ScriptPackageFile> EnumerateCSharpSources(this ScriptPackageSpec? package) =>
        package == null
            ? Array.Empty<ScriptPackageFile>()
            : package.CsharpSources;

    public static IEnumerable<ScriptPackageFile> EnumerateProtoSources(this ScriptPackageSpec? package) =>
        package == null
            ? Array.Empty<ScriptPackageFile>()
            : package.ProtoFiles;

    public static bool HasProtoSources(this ScriptPackageSpec? package) =>
        package != null && package.ProtoFiles.Count > 0;

    public static ScriptPackageSpec ClonePackage(this ScriptPackageSpec? package) =>
        package?.Clone() ?? new ScriptPackageSpec();
}
