namespace Aevatar.AI.ToolProviders.Ornn.Publishing;

public static class OrnnSkillAssetPathPolicy
{
    private static readonly char[] Separators = ['/', '\\'];

    public static (string? PackagePath, OrnnSkillPublishDiagnostic? Diagnostic) NormalizeWorkflowPath(
        string workflowId)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", "workflow_id is required."));
        if (workflowId.IndexOfAny(Separators) >= 0 ||
            workflowId.Contains("..", StringComparison.Ordinal) ||
            workflowId.Contains('.', StringComparison.Ordinal))
        {
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", "workflow_id must be a path-safe file stem."));
        }

        return ($"workflows/{workflowId.Trim()}.yaml", null);
    }

    public static (string? PackagePath, OrnnSkillPublishDiagnostic? Diagnostic) NormalizeScriptPath(string path) =>
        NormalizeUnderRoot("scripts", path, rejectYaml: false, rejectReservedWorkflowSubtree: false);

    public static (string? PackagePath, OrnnSkillPublishDiagnostic? Diagnostic) NormalizeReferencePath(string path) =>
        NormalizeUnderRoot("references", path, rejectYaml: false, rejectReservedWorkflowSubtree: false);

    public static (string? PackagePath, OrnnSkillPublishDiagnostic? Diagnostic) NormalizeAssetPath(string path) =>
        NormalizeUnderRoot("assets", path, rejectYaml: true, rejectReservedWorkflowSubtree: true);

    private static (string? PackagePath, OrnnSkillPublishDiagnostic? Diagnostic) NormalizeUnderRoot(
        string root,
        string path,
        bool rejectYaml,
        bool rejectReservedWorkflowSubtree)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", "path is required."));

        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(":", StringComparison.Ordinal) ||
            normalized.Contains("//", StringComparison.Ordinal))
        {
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", $"'{path}' must be a relative path."));
        }

        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(static x => x.Trim())
            .ToArray();
        if (segments.Length == 0)
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", "path is required."));

        if (string.Equals(segments[0], root, StringComparison.OrdinalIgnoreCase))
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", $"path must be relative to {root}/, not include the root folder."));

        foreach (var segment in segments)
        {
            if (segment is "." or ".." ||
                segment.Contains('\0', StringComparison.Ordinal))
            {
                return (null, new OrnnSkillPublishDiagnostic("invalid_path", $"'{path}' must not traverse directories."));
            }
        }

        if (segments.Length == 1 &&
            string.Equals(segments[0], "README.md", StringComparison.OrdinalIgnoreCase))
        {
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", $"{root}/README.md is not allowed as a root override."));
        }

        if (segments.Any(static x => string.Equals(x, "SKILL.md", StringComparison.OrdinalIgnoreCase)))
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", "SKILL.md overrides are not allowed."));

        var relative = string.Join('/', segments);
        var packagePath = $"{root}/{relative}";
        if (rejectReservedWorkflowSubtree &&
            packagePath.StartsWith("assets/workflows/", StringComparison.OrdinalIgnoreCase))
        {
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", "assets/workflows/ is reserved."));
        }

        if (rejectYaml &&
            (packagePath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
             packagePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, new OrnnSkillPublishDiagnostic("invalid_path", "ordinary assets[] cannot publish .yaml or .yml files."));
        }

        return (packagePath, null);
    }
}
