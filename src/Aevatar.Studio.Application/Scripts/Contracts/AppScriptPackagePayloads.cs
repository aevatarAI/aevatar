using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;

namespace Aevatar.Studio.Application.Scripts.Contracts;

public sealed record AppScriptPackageFile(
    string? Path,
    string? Content);

public sealed record AppScriptPackage(
    IReadOnlyList<AppScriptPackageFile>? CsharpSources,
    IReadOnlyList<AppScriptPackageFile>? ProtoFiles,
    string? EntryBehaviorTypeName,
    string? EntrySourcePath);

public static class AppScriptPackagePayloads
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Studio save serialized multi-file packages into sourceText JSON before passing them into scope/definition commands.
    //   New principle: Studio save converts external payloads to ScriptPackageSpec at the adapter boundary; JSON serializer is presentation compatibility only.
    public static bool HasFiles(AppScriptPackage? package) =>
        (package?.CsharpSources?.Count ?? 0) > 0 ||
        (package?.ProtoFiles?.Count ?? 0) > 0;

    public static ScriptPackageSpec ResolvePackage(
        AppScriptPackage? package,
        string? sourceText)
    {
        if (!HasFiles(package))
            return ScriptPackageSpecExtensions.CreateSingleSource(sourceText ?? string.Empty);

        return NormalizePackage(package!);
    }

    public static string ResolvePrimarySourceText(
        AppScriptPackage? package,
        string? sourceText) =>
        ResolvePackage(package, sourceText).GetPrimaryCSharpSource();

    public static string ResolvePersistedSource(
        AppScriptPackage? package,
        string? sourceText)
    {
        // Presentation compatibility only: external clients that still persist a
        // single source string can receive a derived source representation here.
        if (!HasFiles(package))
            return sourceText ?? string.Empty;

        var normalizedPackage = ScriptPackageModel.ToSourcePackage(NormalizePackage(package!));
        return normalizedPackage.CSharpSources.Count == 1 &&
               normalizedPackage.ProtoFiles.Count == 0 &&
               string.IsNullOrWhiteSpace(normalizedPackage.EntryBehaviorTypeName)
            ? normalizedPackage.CSharpSources[0].Content ?? string.Empty
            : ScriptSourcePackageSerializer.Serialize(normalizedPackage);
    }

    public static string ComputeSourceHash(
        AppScriptPackage? package,
        string? sourceText)
    {
        return ScriptPackageModel.ComputePackageHash(ResolvePackage(package, sourceText));
    }

    private static ScriptPackageSpec NormalizePackage(AppScriptPackage package)
    {
        var csharpSources = NormalizeFiles(package.CsharpSources, "Behavior.cs");
        var protoFiles = NormalizeFiles(package.ProtoFiles, "schema.proto");
        var entrySourcePath = ResolveEntrySourcePath(package.EntrySourcePath, csharpSources);

        var spec = new ScriptPackageSpec
        {
            EntryBehaviorTypeName = package.EntryBehaviorTypeName?.Trim() ?? string.Empty,
            EntrySourcePath = entrySourcePath,
        };

        foreach (var file in csharpSources)
            spec.CsharpSources.Add(file);
        foreach (var file in protoFiles)
            spec.ProtoFiles.Add(file);

        return spec;
    }

    private static IReadOnlyList<ScriptPackageFile> NormalizeFiles(
        IReadOnlyList<AppScriptPackageFile>? files,
        string defaultPath)
    {
        if (files == null || files.Count == 0)
            return Array.Empty<ScriptPackageFile>();

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file == null)
                continue;

            var candidatePath = string.IsNullOrWhiteSpace(file.Path)
                ? defaultPath
                : file.Path.Trim();
            var path = ScriptPackageModel.NormalizeRelativePath(candidatePath);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            normalized[path] = file.Content ?? string.Empty;
        }

        return normalized
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new ScriptPackageFile
            {
                Path = pair.Key,
                Content = pair.Value,
            })
            .ToArray();
    }

    private static string ResolveEntrySourcePath(
        string? requestedEntryPath,
        IReadOnlyList<ScriptPackageFile> csharpSources)
    {
        if (csharpSources.Count == 0)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(requestedEntryPath))
        {
            var normalized = ScriptPackageModel.NormalizeRelativePath(requestedEntryPath);
            if (csharpSources.Any(file => string.Equals(file.Path, normalized, StringComparison.Ordinal)))
                return normalized;
        }

        return csharpSources[0].Path ?? "Behavior.cs";
    }
}
