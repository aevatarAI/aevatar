using System.Text;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Aevatar.AI.ToolProviders.Ornn.AgentProfiles;

public sealed class OrnnAgentProfileSkillPackageMapper
{
    private const string InvalidPackageCode = "INVALID_SKILL_PACKAGE";
    private const string InvalidPackageMessage = "Exact Ornn skill package is invalid.";

    private readonly OrnnSkillPublishValidationPipeline _validationPipeline;
    private readonly SkillFrontmatterParser _frontmatterParser = new();
    private readonly SkillWorkflowExtractor _workflowExtractor = new();
    private readonly SkillScriptExtractor _scriptExtractor = new();
    private readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public OrnnAgentProfileSkillPackageMapper(
        OrnnSkillPublishValidationPipeline validationPipeline)
    {
        _validationPipeline = validationPipeline ??
                              throw new ArgumentNullException(nameof(validationPipeline));
    }

    internal async Task<ExactOrnnSkillResolutionResult> MapAsync(
        OrnnExactSkillDetail detail,
        OrnnSkillJson json,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(json);

        if (!TryNormalizeFiles(json.Files, out var files, out var path))
            return InvalidPackage(path);

        var skillMarkdownEntries = files
            .Where(static entry => string.Equals(
                entry.Key,
                "SKILL.md",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (skillMarkdownEntries.Length != 1)
            return InvalidPackage("SKILL.md");

        var skillMarkdown = skillMarkdownEntries[0].Value;
        if (!TryParseFrontmatter(skillMarkdown, out var structured))
            return InvalidPackage("SKILL.md");

        if (!string.Equals(structured.Name, detail.Name, StringComparison.Ordinal) ||
            !string.Equals(structured.Version, json.Version, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(structured.Metadata?.Category))
        {
            return InvalidPackage("SKILL.md");
        }

        if (!TryExtractPackageContent(
                files,
                skillMarkdown,
                detail.Name ?? string.Empty,
                out var extracted,
                out path))
        {
            return InvalidPackage(path);
        }

        var request = BuildValidationRequest(
            detail,
            json,
            structured,
            extracted);
        OrnnSkillPublishValidationResult validation;
        try
        {
            validation = await _validationPipeline.ValidateAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InvalidPackage();
        }

        if (!validation.IsValid)
            return InvalidPackage(NormalizeDiagnosticPath(validation.Diagnostics[0].Path));

        var package = new ResolvedOrnnSkillPackage
        {
            SkillGuid = detail.Guid,
            LiteralVersion = json.Version,
            CanonicalName = detail.Name,
            PublisherId = detail.CreatedBy,
            UpstreamSkillHash = detail.SkillHash,
            Description = extracted.Frontmatter.Description ?? json.Description ?? string.Empty,
            Instructions = extracted.Frontmatter.Body,
            Arguments = extracted.Frontmatter.Arguments ?? string.Empty,
            WhenToUse = extracted.Frontmatter.WhenToUse ?? string.Empty,
            ModelInvocable = extracted.Frontmatter.IsModelInvocable,
            UserInvocable = extracted.Frontmatter.IsUserInvocable,
        };
        package.DeclaredToolNames.Add(structured.Metadata?.ToolList ?? []);
        package.Workflows.Add(extracted.Workflows.Select(ToProfileWorkflow));
        package.Scripts.Add(extracted.Scripts.Select(ToProfileScript));
        package.References.Add(extracted.References);
        package.Assets.Add(extracted.Assets);

        try
        {
            return ExactOrnnSkillResolutionResult.Success(
                AgentProfileDeterminism.NormalizeResolvedSkillPackage(package));
        }
        catch (AgentProfileContractValidationException)
        {
            return InvalidPackage();
        }
    }

    private bool TryExtractPackageContent(
        IReadOnlyDictionary<string, string> files,
        string skillMarkdown,
        string skillName,
        out ExtractedPackageContent extracted,
        out string path)
    {
        extracted = null!;
        var associatedFiles = files
            .Where(static entry => !string.Equals(
                entry.Key,
                "SKILL.md",
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);

        if (!ValidateAssociatedFileShapes(associatedFiles, out path))
            return false;

        var canonicalPaths = new HashSet<string>(StringComparer.Ordinal);

        var workflowExtraction = _workflowExtractor.ExtractFromFiles(associatedFiles);
        var workflowFileCount = associatedFiles.Keys.Count(IsWorkflowPath);
        if (workflowFileCount > 0 &&
            workflowExtraction.Workflows.Sum(static workflow => workflow.WorkflowYamls.Count) != workflowFileCount)
        {
            path = "workflows";
            return false;
        }

        if (!TryNormalizeWorkflows(
                workflowExtraction.Workflows,
                canonicalPaths,
                out var workflows,
                out path))
        {
            return false;
        }

        var parsed = _frontmatterParser.Parse(skillMarkdown);
        var scriptExtraction = _scriptExtractor.ExtractFromFiles(
            parsed.Name ?? skillName,
            parsed.ScriptEntry,
            workflowExtraction.RemainingFiles);
        if (scriptExtraction.RemainingFiles?.Keys.Any(IsScriptPath) == true)
        {
            path = "scripts";
            return false;
        }

        if (!TryNormalizeScripts(
                scriptExtraction.Scripts,
                canonicalPaths,
                out var scripts,
                out path))
        {
            return false;
        }

        var references = new List<AgentProfileNamedTextAsset>();
        var assets = new List<AgentProfileNamedTextAsset>();
        foreach (var (assetPath, content) in scriptExtraction.RemainingFiles ??
                                                   new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (TryMapNamedAsset(
                    assetPath,
                    content,
                    canonicalPaths,
                    references,
                    assets,
                    out path))
            {
                continue;
            }

            return false;
        }

        extracted = new ExtractedPackageContent(
            parsed,
            workflows,
            scripts,
            references,
            assets);
        path = string.Empty;
        return true;
    }

    private static OrnnSkillPublishRequest BuildValidationRequest(
        OrnnExactSkillDetail detail,
        OrnnSkillJson json,
        SkillFrontmatterDocument structured,
        ExtractedPackageContent extracted) =>
        new()
        {
            Name = detail.Name ?? string.Empty,
            Description = extracted.Frontmatter.Description ?? json.Description ?? string.Empty,
            Version = json.Version ?? string.Empty,
            Category = structured.Metadata?.Category ?? string.Empty,
            InstructionsMarkdown = extracted.Frontmatter.Body,
            Tags = structured.Metadata?.Tags ?? [],
            OutputType = structured.Metadata?.OutputType,
            Runtimes = structured.Metadata?.Runtimes ?? [],
            RuntimeDependencies = structured.Metadata?.RuntimeDependencies ?? [],
            RuntimeEnvVars = structured.Metadata?.RuntimeEnvVars ?? [],
            ToolList = structured.Metadata?.ToolList ?? [],
            WorkflowYamls = extracted.Workflows
                .SelectMany(static workflow => workflow.WorkflowYamls.Select(content =>
                    new OrnnSkillPublishWorkflowYaml
                    {
                        WorkflowId = workflow.WorkflowId,
                        Content = content,
                    }))
                .OrderBy(static workflow => workflow.WorkflowId, StringComparer.Ordinal)
                .ToArray(),
            Scripts = extracted.Scripts
                .SelectMany(static script => script.SourceFiles.Concat(script.ProtoFiles))
                .Select(static file => new OrnnSkillPublishScript
                {
                    Path = file.Key,
                    Content = file.Value,
                })
                .OrderBy(static script => script.Path, StringComparer.Ordinal)
                .ToArray(),
            References = extracted.References.Select(static asset => new OrnnSkillPublishFile
            {
                Path = asset.Path["references/".Length..],
                Content = asset.Content,
            }).ToArray(),
            Assets = extracted.Assets.Select(static asset => new OrnnSkillPublishFile
            {
                Path = asset.Path["assets/".Length..],
                Content = asset.Content,
            }).ToArray(),
        };

    private static bool TryNormalizeFiles(
        IReadOnlyDictionary<string, string>? input,
        out SortedDictionary<string, string> files,
        out string path)
    {
        files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        path = string.Empty;
        if (input is null || input.Count == 0)
            return false;

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rawPath, content) in input)
        {
            if (!TryNormalizeIncomingPath(rawPath, out var normalizedPath) ||
                !identities.Add(normalizedPath))
            {
                path = NormalizeDiagnosticPath(rawPath);
                return false;
            }

            files.Add(normalizedPath, content ?? string.Empty);
        }

        return true;
    }

    private static bool TryNormalizeIncomingPath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            return false;
        }

        normalized = value.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal) ||
            normalized.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = normalized.Split('/');
        return segments.Length > 0 &&
               segments.All(static segment =>
                   segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool ValidateAssociatedFileShapes(
        IReadOnlyDictionary<string, string> files,
        out string path)
    {
        path = string.Empty;
        foreach (var assetPath in files.Keys)
        {
            if (IsWorkflowPath(assetPath))
            {
                if (assetPath.Count(static character => character == '/') == 1 &&
                    (assetPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                     assetPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }
            else if (IsScriptPath(assetPath))
            {
                if (assetPath.Count(static character => character == '/') == 1 &&
                    (assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                     assetPath.EndsWith(".proto", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }
            else if (assetPath.StartsWith("references/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            else if (assetPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            path = assetPath;
            return false;
        }

        return true;
    }

    private static bool TryMapNamedAsset(
        string path,
        string content,
        HashSet<string> canonicalPaths,
        ICollection<AgentProfileNamedTextAsset> references,
        ICollection<AgentProfileNamedTextAsset> assets,
        out string diagnosticPath)
    {
        if (path.StartsWith("references/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = path["references/".Length..];
            if (!TryAddCanonicalPath(
                    relative,
                    OrnnSkillAssetPathPolicy.NormalizeReferencePath,
                    path,
                    canonicalPaths,
                    out var normalized,
                    out diagnosticPath))
            {
                return false;
            }

            references.Add(new AgentProfileNamedTextAsset { Path = normalized, Content = content });
            return true;
        }

        if (!path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            diagnosticPath = path;
            return false;
        }

        var assetRelative = path["assets/".Length..];
        if (!TryAddCanonicalPath(
                assetRelative,
                OrnnSkillAssetPathPolicy.NormalizeAssetPath,
                path,
                canonicalPaths,
                out var assetNormalized,
                out diagnosticPath))
        {
            return false;
        }

        assets.Add(new AgentProfileNamedTextAsset { Path = assetNormalized, Content = content });
        return true;
    }

    private static bool TryNormalizeWorkflows(
        IReadOnlyList<SkillWorkflowDescriptor> workflows,
        HashSet<string> canonicalPaths,
        out IReadOnlyList<SkillWorkflowDescriptor> normalizedWorkflows,
        out string diagnosticPath)
    {
        var normalized = new List<SkillWorkflowDescriptor>(workflows.Count);
        foreach (var workflow in workflows)
        {
            var fallbackPath = $"workflows/{workflow.WorkflowId}.yaml";
            if (!TryAddCanonicalPath(
                    workflow.WorkflowId,
                    OrnnSkillAssetPathPolicy.NormalizeWorkflowPath,
                    fallbackPath,
                    canonicalPaths,
                    out var packagePath,
                    out diagnosticPath))
            {
                normalizedWorkflows = [];
                return false;
            }

            normalized.Add(new SkillWorkflowDescriptor
            {
                WorkflowId = packagePath["workflows/".Length..^".yaml".Length],
                WorkflowYamls = workflow.WorkflowYamls,
            });
        }

        normalizedWorkflows = normalized;
        diagnosticPath = string.Empty;
        return true;
    }

    private static bool TryNormalizeScripts(
        IReadOnlyList<SkillScriptDescriptor> scripts,
        HashSet<string> canonicalPaths,
        out IReadOnlyList<SkillScriptDescriptor> normalizedScripts,
        out string diagnosticPath)
    {
        var normalized = new List<SkillScriptDescriptor>(scripts.Count);
        foreach (var script in scripts)
        {
            if (!TryNormalizeScriptFiles(
                    script.SourceFiles,
                    canonicalPaths,
                    out var sourceFiles,
                    out diagnosticPath) ||
                !TryNormalizeScriptFiles(
                    script.ProtoFiles,
                    canonicalPaths,
                    out var protoFiles,
                    out diagnosticPath))
            {
                normalizedScripts = [];
                return false;
            }

            normalized.Add(new SkillScriptDescriptor
            {
                ScriptId = script.ScriptId,
                SourceFiles = sourceFiles,
                ProtoFiles = protoFiles,
                EntryBehaviorTypeName = script.EntryBehaviorTypeName,
            });
        }

        normalizedScripts = normalized;
        diagnosticPath = string.Empty;
        return true;
    }

    private static bool TryNormalizeScriptFiles(
        IReadOnlyDictionary<string, string> files,
        HashSet<string> canonicalPaths,
        out IReadOnlyDictionary<string, string> normalizedFiles,
        out string diagnosticPath)
    {
        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            var relative = IsScriptPath(path)
                ? path["scripts/".Length..]
                : path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
                    ? path["assets/".Length..]
                    : string.Empty;
            if (relative.Length == 0)
            {
                normalizedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
                diagnosticPath = path;
                return false;
            }

            if (!TryAddCanonicalPath(
                    relative,
                    OrnnSkillAssetPathPolicy.NormalizeScriptPath,
                    path,
                    canonicalPaths,
                    out var packagePath,
                    out diagnosticPath))
            {
                normalizedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
                return false;
            }

            normalized.Add(packagePath, content);
        }

        normalizedFiles = normalized;
        diagnosticPath = string.Empty;
        return true;
    }

    private static bool TryAddCanonicalPath(
        string policyInput,
        Func<string, (string? PackagePath, OrnnSkillPublishDiagnostic? Diagnostic)> normalize,
        string fallbackPath,
        HashSet<string> canonicalPaths,
        out string canonicalPath,
        out string diagnosticPath)
    {
        var (normalized, diagnostic) = normalize(policyInput.Normalize(NormalizationForm.FormC));
        if (diagnostic is not null || normalized is null)
        {
            canonicalPath = string.Empty;
            diagnosticPath = fallbackPath;
            return false;
        }

        canonicalPath = normalized;
        diagnosticPath = normalized;
        return canonicalPaths.Add(normalized);
    }

    private bool TryParseFrontmatter(string content, out SkillFrontmatterDocument document)
    {
        document = new SkillFrontmatterDocument();
        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimStart();
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return false;

        var closing = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (closing < 0)
            return false;

        try
        {
            document = _yamlDeserializer.Deserialize<SkillFrontmatterDocument>(normalized[4..closing]) ??
                       new SkillFrontmatterDocument();
            return document.Metadata is not null &&
                   ValidateStringList(document.Metadata.Tags) &&
                   ValidateStringList(document.Metadata.Runtimes) &&
                   ValidateStringList(document.Metadata.RuntimeDependencies) &&
                   ValidateStringList(document.Metadata.RuntimeEnvVars) &&
                   ValidateStringList(document.Metadata.ToolList);
        }
        catch (YamlException)
        {
            return false;
        }
    }

    private static bool ValidateStringList(IReadOnlyList<string>? values) =>
        values is null || values.All(static value => !string.IsNullOrWhiteSpace(value));

    private static AgentProfileWorkflowAsset ToProfileWorkflow(SkillWorkflowDescriptor workflow)
    {
        var result = new AgentProfileWorkflowAsset { WorkflowId = workflow.WorkflowId };
        result.WorkflowYamls.Add(workflow.WorkflowYamls);
        return result;
    }

    private static AgentProfileScriptAsset ToProfileScript(SkillScriptDescriptor script)
    {
        var result = new AgentProfileScriptAsset
        {
            ScriptId = script.ScriptId,
            EntryBehaviorTypeName = script.EntryBehaviorTypeName,
        };
        result.SourceFiles.Add(script.SourceFiles.Select(static file => new AgentProfileNamedTextAsset
        {
            Path = file.Key,
            Content = file.Value,
        }));
        result.ProtoFiles.Add(script.ProtoFiles.Select(static file => new AgentProfileNamedTextAsset
        {
            Path = file.Key,
            Content = file.Value,
        }));
        return result;
    }

    private static bool IsWorkflowPath(string path) =>
        path.StartsWith("workflows/", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptPath(string path) =>
        path.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDiagnosticPath(string? path)
    {
        if (!TryNormalizeIncomingPath(path, out var normalized))
            return string.Empty;
        return normalized.Length <= 512 ? normalized : string.Empty;
    }

    private static ExactOrnnSkillResolutionResult InvalidPackage(string path = "") =>
        ExactOrnnSkillResolutionResult.Failed(
            InvalidPackageCode,
            InvalidPackageMessage,
            NormalizeDiagnosticPath(path));

    private sealed record ExtractedPackageContent(
        SkillParseResult Frontmatter,
        IReadOnlyList<SkillWorkflowDescriptor> Workflows,
        IReadOnlyList<SkillScriptDescriptor> Scripts,
        IReadOnlyList<AgentProfileNamedTextAsset> References,
        IReadOnlyList<AgentProfileNamedTextAsset> Assets);

    private sealed class SkillFrontmatterDocument
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "version")]
        public string? Version { get; set; }

        [YamlMember(Alias = "metadata")]
        public SkillFrontmatterMetadata? Metadata { get; set; }
    }

    private sealed class SkillFrontmatterMetadata
    {
        [YamlMember(Alias = "category")]
        public string? Category { get; set; }

        [YamlMember(Alias = "tag")]
        public List<string>? Tags { get; set; }

        [YamlMember(Alias = "output-type")]
        public string? OutputType { get; set; }

        [YamlMember(Alias = "runtime")]
        public List<string>? Runtimes { get; set; }

        [YamlMember(Alias = "runtime-dependency")]
        public List<string>? RuntimeDependencies { get; set; }

        [YamlMember(Alias = "runtime-env-var")]
        public List<string>? RuntimeEnvVars { get; set; }

        [YamlMember(Alias = "tool-list")]
        public List<string>? ToolList { get; set; }
    }
}
