using System.Security.Cryptography;
using System.Text;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;

namespace Aevatar.Tools.Cli.Hosting;

public sealed record AppScriptPackageFile(
    string? Path,
    string? Content);

public sealed record AppScriptPackage(
    IReadOnlyList<AppScriptPackageFile>? CsharpSources,
    IReadOnlyList<AppScriptPackageFile>? ProtoFiles,
    string? EntryBehaviorTypeName,
    string? EntrySourcePath);

internal static class AppScriptPackagePayloads
{
    public static bool HasFiles(AppScriptPackage? package) =>
        (package?.CsharpSources?.Count ?? 0) > 0 ||
        (package?.ProtoFiles?.Count ?? 0) > 0;

    public static ScriptPackageSpec ResolvePackage(
        AppScriptPackage? package,
        string? sourceText)
    {
        if (!HasFiles(package))
            return ScriptPackageModel.ToPackageSpec(ScriptSourcePackageSerializer.DeserializeOrWrapCSharp(sourceText ?? string.Empty));

        return NormalizePackage(package!);
    }

    public static string ResolvePersistedSource(
        AppScriptPackage? package,
        string? sourceText)
    {
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
        if (!HasFiles(package))
        {
            var bytes = Encoding.UTF8.GetBytes(sourceText ?? string.Empty);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        return ScriptPackageModel.ComputePackageHash(NormalizePackage(package!));
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
