using Aevatar.Scripting.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Core.Compilation;

public static class ScriptPackageModel
{
    public static ScriptPackageSpec ToPackageSpec(ScriptSourcePackage? package)
    {
        var normalized = (package ?? ScriptSourcePackage.Empty).Normalize();
        var spec = new ScriptPackageSpec
        {
            EntryBehaviorTypeName = normalized.EntryBehaviorTypeName ?? string.Empty,
            EntrySourcePath = normalized.CSharpSources.FirstOrDefault()?.Path ?? "Behavior.cs",
        };
        foreach (var source in normalized.CSharpSources)
        {
            spec.CsharpSources.Add(new ScriptPackageFile
            {
                Path = source.NormalizedPath,
                Content = source.Content ?? string.Empty,
            });
        }

        foreach (var proto in normalized.ProtoFiles)
        {
            spec.ProtoFiles.Add(new ScriptPackageFile
            {
                Path = proto.NormalizedPath,
                Content = proto.Content ?? string.Empty,
            });
        }

        return spec;
    }

    public static ScriptSourcePackage ToSourcePackage(ScriptPackageSpec? package)
    {
        if (package == null)
            return ScriptSourcePackage.Empty;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            package.EnumerateCSharpSources()
                .Select(static file => new ScriptSourceFile(
                    file.Path ?? string.Empty,
                    file.Content ?? string.Empty))
                .ToArray(),
            package.EnumerateProtoSources()
                .Select(static file => new ScriptSourceFile(
                    file.Path ?? string.Empty,
                    file.Content ?? string.Empty))
                .ToArray(),
            package.EntryBehaviorTypeName ?? string.Empty).Normalize();
    }

    public static ScriptPackageSpec CreateSingleSourcePackage(
        string sourceText,
        string entryBehaviorTypeName = "")
    {
        var package = new ScriptPackageSpec
        {
            EntryBehaviorTypeName = entryBehaviorTypeName ?? string.Empty,
            EntrySourcePath = "Behavior.cs",
        };
        package.CsharpSources.Add(new ScriptPackageFile
        {
            Path = package.EntrySourcePath,
            Content = sourceText ?? string.Empty,
        });
        return package;
    }

    public static string GetEntrySourceText(ScriptPackageSpec? package)
    {
        if (package == null)
            return string.Empty;

        var entryPath = package.EntrySourcePath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(entryPath))
        {
            var entry = package.CsharpSources.FirstOrDefault(x =>
                string.Equals(x.Path, entryPath, StringComparison.Ordinal));
            if (entry != null)
                return entry.Content ?? string.Empty;
        }

        return package.CsharpSources.FirstOrDefault()?.Content ?? string.Empty;
    }

    public static string ComputePackageHash(ScriptPackageSpec? package)
    {
        return ComputePackageHash(ToSourcePackage(package));
    }

    public static string ComputePackageHash(ScriptSourcePackage? package)
    {
        if (package == null)
            return string.Empty;

        var normalized = package.Normalize();
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();
        WriteString(stream, normalized.Format);
        WriteString(stream, normalized.EntryBehaviorTypeName);
        WriteFiles(stream, normalized.CSharpSources);
        WriteFiles(stream, normalized.ProtoFiles);
        stream.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static IReadOnlyList<ScriptPackageFile> GetNormalizedCSharpSources(ScriptPackageSpec? package) =>
        NormalizeFiles(package?.CsharpSources);

    public static IReadOnlyList<ScriptPackageFile> GetNormalizedProtoFiles(ScriptPackageSpec? package) =>
        NormalizeFiles(package?.ProtoFiles);

    private static IReadOnlyList<ScriptPackageFile> NormalizeFiles(IEnumerable<ScriptPackageFile>? files)
    {
        if (files == null)
            return Array.Empty<ScriptPackageFile>();

        return files
            .Where(static x => x != null)
            .Select(static file => new ScriptPackageFile
            {
                Path = NormalizeRelativePath(file.Path),
                Content = file.Content ?? string.Empty,
            })
            .Where(static x => !string.IsNullOrWhiteSpace(x.Path))
            .OrderBy(static x => x.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static void WriteFiles(Stream stream, IEnumerable<ScriptPackageFile>? files)
    {
        foreach (var file in NormalizeFiles(files))
        {
            WriteString(stream, file.Path);
            WriteString(stream, file.Content);
        }
    }

    private static void WriteFiles(Stream stream, IEnumerable<ScriptSourceFile>? files)
    {
        foreach (var file in files ?? Array.Empty<ScriptSourceFile>())
        {
            WriteString(stream, ScriptSourceFile.NormalizePath(file.Path));
            WriteString(stream, file.Content);
        }
    }

    private static void WriteString(Stream stream, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var lengthBytes = BitConverter.GetBytes(bytes.Length);
        stream.Write(lengthBytes, 0, lengthBytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    public static string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path
            .Replace('\\', '/')
            .Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException($"Script package path `{path}` must be relative.");
        if (normalized.Contains("../", StringComparison.Ordinal) ||
            string.Equals(normalized, "..", StringComparison.Ordinal))
            throw new InvalidOperationException($"Script package path `{path}` cannot traverse parent directories.");
        return normalized;
    }
}
