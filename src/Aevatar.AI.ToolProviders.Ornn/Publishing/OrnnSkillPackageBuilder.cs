using System.IO.Compression;
using System.Text;

namespace Aevatar.AI.ToolProviders.Ornn.Publishing;

public sealed class OrnnSkillPackageBuilder
{
    private static readonly DateTimeOffset StableTimestamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public (OrnnSkillPackageBuildResult? Package, OrnnSkillPublishValidationResult Validation) Build(
        OrnnSkillPublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var diagnostics = new List<OrnnSkillPublishDiagnostic>();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        AddFile(files, $"{request.Name}/SKILL.md", BuildSkillMarkdown(request), diagnostics);

        foreach (var workflow in request.WorkflowYamls)
        {
            var (path, diagnostic) = OrnnSkillAssetPathPolicy.NormalizeWorkflowPath(workflow.WorkflowId);
            AddPathResult(files, request.Name, path, diagnostic, workflow.Content, diagnostics);
        }

        foreach (var script in request.Scripts)
        {
            var (path, diagnostic) = OrnnSkillAssetPathPolicy.NormalizeScriptPath(script.Path);
            AddPathResult(files, request.Name, path, diagnostic, script.Content, diagnostics);
        }

        foreach (var reference in request.References)
        {
            var (path, diagnostic) = OrnnSkillAssetPathPolicy.NormalizeReferencePath(reference.Path);
            AddPathResult(files, request.Name, path, diagnostic, reference.Content, diagnostics);
        }

        foreach (var asset in request.Assets)
        {
            var (path, diagnostic) = OrnnSkillAssetPathPolicy.NormalizeAssetPath(asset.Path);
            AddPathResult(files, request.Name, path, diagnostic, asset.Content, diagnostics);
        }

        if (diagnostics.Count > 0)
            return (null, new OrnnSkillPublishValidationResult(diagnostics));

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = StableTimestamp;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
        }

        return (new OrnnSkillPackageBuildResult(stream.ToArray(), files), OrnnSkillPublishValidationResult.Success);
    }

    private static void AddPathResult(
        IDictionary<string, string> files,
        string rootName,
        string? packagePath,
        OrnnSkillPublishDiagnostic? diagnostic,
        string content,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (diagnostic != null)
        {
            diagnostics.Add(diagnostic);
            return;
        }

        AddFile(files, $"{rootName}/{packagePath}", content, diagnostics);
    }

    private static void AddFile(
        IDictionary<string, string> files,
        string packagePath,
        string content,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (files.ContainsKey(packagePath))
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic(
                "duplicate_path",
                $"Duplicate package path '{packagePath}'.",
                packagePath));
            return;
        }

        files.Add(packagePath, content ?? string.Empty);
    }

    private static string BuildSkillMarkdown(OrnnSkillPublishRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"name: {request.Name}");
        builder.AppendLine($"description: {YamlQuote(request.Description)}");
        builder.AppendLine("metadata:");
        builder.AppendLine($"  category: {request.Category}");
        AppendArray(builder, "tag", request.Tags, indent: "  ");
        if (!string.IsNullOrWhiteSpace(request.OutputType))
            builder.AppendLine($"  output-type: {request.OutputType}");
        AppendArray(builder, "runtime", request.Runtimes, indent: "  ");
        AppendArray(builder, "runtime-dependency", request.RuntimeDependencies, indent: "  ");
        AppendArray(builder, "runtime-env-var", request.RuntimeEnvVars, indent: "  ");
        AppendArray(builder, "tool-list", request.ToolList, indent: "  ");
        builder.AppendLine($"version: {YamlQuote(request.Version)}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append((request.InstructionsMarkdown ?? string.Empty).Trim());
        builder.AppendLine();
        return builder.ToString();
    }

    private static void AppendArray(
        StringBuilder builder,
        string key,
        IReadOnlyList<string> values,
        string indent)
    {
        if (values.Count == 0)
            return;

        builder.AppendLine($"{indent}{key}:");
        foreach (var value in values)
            builder.AppendLine($"{indent}  - {YamlQuote(value)}");
    }

    private static string YamlQuote(string value) =>
        "\"" + (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
