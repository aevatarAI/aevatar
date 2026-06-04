using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.AI.ToolProviders.Ornn.Publishing;

public sealed record OrnnSkillPublishRequest
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string Category { get; init; }
    public required string InstructionsMarkdown { get; init; }
    public string? Visibility { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? OutputType { get; init; }
    public IReadOnlyList<string> Runtimes { get; init; } = [];
    public IReadOnlyList<string> RuntimeDependencies { get; init; } = [];
    public IReadOnlyList<string> RuntimeEnvVars { get; init; } = [];
    public IReadOnlyList<string> ToolList { get; init; } = [];
    public IReadOnlyList<OrnnSkillPublishWorkflowYaml> WorkflowYamls { get; init; } = [];
    public IReadOnlyList<OrnnSkillPublishScript> Scripts { get; init; } = [];
    public IReadOnlyList<OrnnSkillPublishFile> References { get; init; } = [];
    public IReadOnlyList<OrnnSkillPublishFile> Assets { get; init; } = [];

    public bool HasWorkflowYamls => WorkflowYamls.Count > 0;
    public bool HasScripts => Scripts.Count > 0;
}

public sealed record OrnnSkillPublishWorkflowYaml
{
    public required string WorkflowId { get; init; }
    public required string Content { get; init; }
}

public sealed record OrnnSkillPublishScript
{
    public required string Path { get; init; }
    public required string Content { get; init; }
}

public sealed record OrnnSkillPublishFile
{
    public required string Path { get; init; }
    public required string Content { get; init; }
}

public sealed record OrnnSkillPublishDiagnostic(string Code, string Message, string? Path = null);

public sealed record OrnnSkillPublishValidationResult(IReadOnlyList<OrnnSkillPublishDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;

    public static OrnnSkillPublishValidationResult Success { get; } = new([]);
}

public sealed record OrnnSkillPackageBuildResult(
    byte[] ZipBytes,
    IReadOnlyDictionary<string, string> Files);

public sealed record OrnnSkillPackageFormatViolation(string Rule, string Message);

public sealed record OrnnSkillPackageFormatValidationResult(
    bool IsValid,
    IReadOnlyList<OrnnSkillPackageFormatViolation> Violations,
    string? Error = null)
{
    public static OrnnSkillPackageFormatValidationResult Valid { get; } = new(true, []);
}

public sealed record OrnnSkillPublishResponse(
    bool Succeeded,
    string RawResponse,
    string? Error = null);

public static partial class OrnnSkillPublishRequestParser
{
    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "name",
        "description",
        "version",
        "category",
        "instructions_markdown",
        "visibility",
        "tags",
        "output_type",
        "runtimes",
        "runtime_dependencies",
        "runtime_env_vars",
        "tool_list",
        "workflow_yamls",
        "scripts",
        "references",
        "assets",
    };

    private static readonly HashSet<string> WorkflowFields = new(StringComparer.Ordinal)
    {
        "workflow_id",
        "content",
    };

    private static readonly HashSet<string> FileFields = new(StringComparer.Ordinal)
    {
        "path",
        "content",
    };

    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "plain",
        "tool-based",
        "runtime-based",
        "mixed",
    };

    private static readonly HashSet<string> OutputTypes = new(StringComparer.Ordinal)
    {
        "text",
        "file",
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillNameRegex();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,29}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex("^[A-Z_][A-Z0-9_]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvVarRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowIdRegex();

    public static (OrnnSkillPublishRequest? Request, IReadOnlyList<OrnnSkillPublishDiagnostic> Diagnostics) Parse(
        string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return (null, [new OrnnSkillPublishDiagnostic("invalid_json", "Arguments JSON is required.")]);

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, [new OrnnSkillPublishDiagnostic("invalid_json", "Arguments must be a JSON object.")]);

            var diagnostics = new List<OrnnSkillPublishDiagnostic>();
            RejectUnknownFields(root, RootFields, "$", diagnostics);

            var request = new OrnnSkillPublishRequest
            {
                Name = RequiredString(root, "name", "$.name", diagnostics),
                Description = RequiredString(root, "description", "$.description", diagnostics),
                Version = RequiredString(root, "version", "$.version", diagnostics),
                Category = RequiredString(root, "category", "$.category", diagnostics),
                InstructionsMarkdown = RequiredString(root, "instructions_markdown", "$.instructions_markdown", diagnostics),
                Visibility = OptionalString(root, "visibility", "$.visibility", diagnostics),
                Tags = OptionalStringArray(root, "tags", "$.tags", diagnostics),
                OutputType = OptionalString(root, "output_type", "$.output_type", diagnostics),
                Runtimes = OptionalStringArray(root, "runtimes", "$.runtimes", diagnostics),
                RuntimeDependencies = OptionalStringArray(root, "runtime_dependencies", "$.runtime_dependencies", diagnostics),
                RuntimeEnvVars = OptionalStringArray(root, "runtime_env_vars", "$.runtime_env_vars", diagnostics),
                ToolList = OptionalStringArray(root, "tool_list", "$.tool_list", diagnostics),
                WorkflowYamls = OptionalWorkflowArray(root, diagnostics),
                Scripts = OptionalScriptArray(root, diagnostics),
                References = OptionalFileArray(root, "references", "$.references", diagnostics),
                Assets = OptionalFileArray(root, "assets", "$.assets", diagnostics),
            };

            ValidateRequest(request, diagnostics);
            return diagnostics.Count == 0
                ? (request, diagnostics)
                : (null, diagnostics);
        }
        catch (JsonException ex)
        {
            return (null, [new OrnnSkillPublishDiagnostic("invalid_json", ex.Message)]);
        }
    }

    private static void ValidateRequest(
        OrnnSkillPublishRequest request,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (!SkillNameRegex().IsMatch(request.Name))
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_name", "name must be kebab-case and at most 64 characters.", "$.name"));
        if (request.Description.Length > 1024)
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_description", "description must be at most 1024 characters.", "$.description"));
        if (request.Description.Contains('<', StringComparison.Ordinal) ||
            request.Description.Contains('>', StringComparison.Ordinal))
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_description", "description must not contain XML angle brackets.", "$.description"));
        if (!VersionRegex().IsMatch(request.Version))
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_version", "version must match <major>.<minor>.", "$.version"));
        if (!Categories.Contains(request.Category))
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_category", "category must be plain, tool-based, runtime-based, or mixed.", "$.category"));
        if (!string.IsNullOrWhiteSpace(request.Visibility) &&
            !string.Equals(request.Visibility, "private", StringComparison.Ordinal))
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_visibility", "v1 only supports omitted visibility or private.", "$.visibility"));
        if (request.InstructionsMarkdown.Contains("\n---", StringComparison.Ordinal) ||
            request.InstructionsMarkdown.TrimStart().StartsWith("---", StringComparison.Ordinal))
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_instructions", "instructions_markdown must not include SKILL.md frontmatter delimiters.", "$.instructions_markdown"));

        ValidateStringSet(request.Tags, TagRegex(), "tags must be lowercase kebab-case strings.", "$.tags", diagnostics);
        ValidateStringSet(request.RuntimeEnvVars, EnvVarRegex(), "runtime_env_vars must be UPPER_SNAKE_CASE.", "$.runtime_env_vars", diagnostics);

        if (!string.IsNullOrWhiteSpace(request.OutputType) && !OutputTypes.Contains(request.OutputType))
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_output_type", "output_type must be text or file.", "$.output_type"));

        var hasRuntimeFields = request.Runtimes.Count > 0 ||
                               request.RuntimeDependencies.Count > 0 ||
                               request.RuntimeEnvVars.Count > 0 ||
                               !string.IsNullOrWhiteSpace(request.OutputType);
        var hasToolFields = request.ToolList.Count > 0;

        switch (request.Category)
        {
            case "plain":
                if (hasRuntimeFields || hasToolFields)
                    diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_category_fields", "plain skills must not include runtime or tool fields.", "$.category"));
                break;
            case "tool-based":
                if (!hasToolFields)
                    diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_tool_list", "tool_list is required for tool-based skills.", "$.tool_list"));
                if (hasRuntimeFields)
                    diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_category_fields", "tool-based skills must not include runtime fields.", "$.category"));
                break;
            case "runtime-based":
                if (request.Runtimes.Count == 0)
                    diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_runtimes", "runtimes is required for runtime-based skills.", "$.runtimes"));
                if (string.IsNullOrWhiteSpace(request.OutputType))
                    diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_output_type", "output_type is required for runtime-based skills.", "$.output_type"));
                if (hasToolFields)
                    diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_category_fields", "runtime-based skills must not include tool_list.", "$.category"));
                break;
            case "mixed":
                if (request.Runtimes.Count == 0)
                    diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_runtimes", "runtimes is required for mixed skills.", "$.runtimes"));
                if (string.IsNullOrWhiteSpace(request.OutputType))
                    diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_output_type", "output_type is required for mixed skills.", "$.output_type"));
                if (!hasToolFields)
                    diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_tool_list", "tool_list is required for mixed skills.", "$.tool_list"));
                break;
        }

        foreach (var workflow in request.WorkflowYamls)
        {
            if (!WorkflowIdRegex().IsMatch(workflow.WorkflowId) ||
                workflow.WorkflowId.Contains('.', StringComparison.Ordinal))
            {
                diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_workflow_id", "workflow_id must be a path-safe file stem.", "$.workflow_yamls"));
            }
        }
    }

    private static void RejectUnknownFields(
        JsonElement element,
        ISet<string> allowedFields,
        string path,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedFields.Contains(property.Name))
                diagnostics.Add(new OrnnSkillPublishDiagnostic("unknown_field", $"Unknown field '{property.Name}'.", $"{path}.{property.Name}"));
        }
    }

    private static string RequiredString(
        JsonElement root,
        string propertyName,
        string path,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_field", $"{propertyName} is required.", path));
            return string.Empty;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", $"{propertyName} must be a string.", path));
            return string.Empty;
        }

        var value = property.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_field", $"{propertyName} must not be empty.", path));
        return value;
    }

    private static string? OptionalString(
        JsonElement root,
        string propertyName,
        string path,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;

        if (property.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", $"{propertyName} must be a string.", path));
            return null;
        }

        var value = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static IReadOnlyList<string> OptionalStringArray(
        JsonElement root,
        string propertyName,
        string path,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return [];

        if (property.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", $"{propertyName} must be an array of strings.", path));
            return [];
        }

        var result = new List<string>();
        var index = 0;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", $"{propertyName} items must be strings.", $"{path}[{index}]"));
            else if (!string.IsNullOrWhiteSpace(item.GetString()))
                result.Add(item.GetString()!.Trim());
            else
                diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", $"{propertyName} items must not be empty.", $"{path}[{index}]"));
            index++;
        }

        return result;
    }

    private static IReadOnlyList<OrnnSkillPublishWorkflowYaml> OptionalWorkflowArray(
        JsonElement root,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("workflow_yamls", out var property) || property.ValueKind == JsonValueKind.Null)
            return [];

        if (property.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", "workflow_yamls must be an array.", "$.workflow_yamls"));
            return [];
        }

        var result = new List<OrnnSkillPublishWorkflowYaml>();
        var index = 0;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", "workflow_yamls items must be objects.", $"$.workflow_yamls[{index}]"));
                index++;
                continue;
            }

            RejectUnknownFields(item, WorkflowFields, $"$.workflow_yamls[{index}]", diagnostics);
            result.Add(new OrnnSkillPublishWorkflowYaml
            {
                WorkflowId = RequiredString(item, "workflow_id", $"$.workflow_yamls[{index}].workflow_id", diagnostics),
                Content = RequiredString(item, "content", $"$.workflow_yamls[{index}].content", diagnostics),
            });
            index++;
        }

        return result;
    }

    private static IReadOnlyList<OrnnSkillPublishScript> OptionalScriptArray(
        JsonElement root,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        var files = OptionalFileArray(root, "scripts", "$.scripts", diagnostics);
        return files.Select(static file => new OrnnSkillPublishScript
        {
            Path = file.Path,
            Content = file.Content,
        }).ToArray();
    }

    private static IReadOnlyList<OrnnSkillPublishFile> OptionalFileArray(
        JsonElement root,
        string propertyName,
        string path,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return [];

        if (property.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", $"{propertyName} must be an array.", path));
            return [];
        }

        var result = new List<OrnnSkillPublishFile>();
        var index = 0;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", $"{propertyName} items must be objects.", $"{path}[{index}]"));
                index++;
                continue;
            }

            RejectUnknownFields(item, FileFields, $"{path}[{index}]", diagnostics);
            result.Add(new OrnnSkillPublishFile
            {
                Path = RequiredString(item, "path", $"{path}[{index}].path", diagnostics),
                Content = RequiredString(item, "content", $"{path}[{index}].content", diagnostics),
            });
            index++;
        }

        return result;
    }

    private static void ValidateStringSet(
        IReadOnlyList<string> values,
        Regex regex,
        string message,
        string path,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        foreach (var value in values)
        {
            if (!regex.IsMatch(value))
                diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_string", message, path));
        }
    }
}
