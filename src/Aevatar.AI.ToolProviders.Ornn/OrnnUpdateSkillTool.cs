using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Ornn.Publishing;

namespace Aevatar.AI.ToolProviders.Ornn;

public sealed class OrnnUpdateSkillTool : IAgentTool
{
    private readonly OrnnSkillPublishValidationPipeline _validationPipeline;
    private readonly OrnnSkillPackageBuilder _packageBuilder;
    private readonly OrnnSkillPackageFormatValidator _formatValidator;
    private readonly OrnnSkillClient _client;

    public OrnnUpdateSkillTool(
        OrnnSkillPublishValidationPipeline validationPipeline,
        OrnnSkillPackageBuilder packageBuilder,
        OrnnSkillPackageFormatValidator formatValidator,
        OrnnSkillClient client)
    {
        _validationPipeline = validationPipeline;
        _packageBuilder = packageBuilder;
        _formatValidator = formatValidator;
        _client = client;
    }

    public string Name => "ornn_update_skill";

    public string Description =>
        "Update an existing Ornn skill package for the current NyxID caller by stable skill_id. " +
        "Before calling, search or GET the current skill JSON, apply the requested full-package change, then submit the complete typed package with the same stable skill_id. " +
        "This validates workflow template YAML, scripts, package format, then uploads the ZIP with PUT by id. " +
        "Workflow YAMLs updated here are Ornn package templates/import sources, not Scope Workflow runtime publication. " +
        "The tool never accepts credentials, service routing fields, raw file maps, metadata bags, public visibility, or skip-validation flags.";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public bool IsReadOnly => false;

    public bool IsDestructive => false;

    public string SideEffectKind => "ornn.update.skill";

    public AgentToolReceipt? CreateSuccessReceipt(string callId, string toolName, string resultJson)
    {
        var updated = ExtractUpdatedSkill(resultJson);
        if (!updated.HasAny)
            return null;

        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.Auto,
            IsDestructive = false,
            SideEffectKind = SideEffectKind,
            SubjectKind = "ornn.skill",
            SubjectId = updated.Guid ?? string.Empty,
            SubjectVersion = updated.Version ?? string.Empty,
            SubjectHash = updated.SkillHash ?? string.Empty,
            ResultJson = resultJson ?? string.Empty,
        };
    }

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "skill_id": { "type": "string", "description": "Stable Ornn skill GUID to update." },
            "name": { "type": "string", "description": "Kebab-case skill package name." },
            "description": { "type": "string", "description": "Short skill description without XML angle brackets." },
            "version": { "type": "string", "description": "Skill version in <major>.<minor> format, for example 1.0." },
            "category": { "type": "string", "enum": ["plain", "tool-based", "runtime-based", "mixed"] },
            "instructions_markdown": { "type": "string", "description": "SKILL.md body content only. Do not include frontmatter delimiters." },
            "visibility": { "type": "string", "enum": ["private"], "description": "Optional. v1 only accepts private." },
            "tags": { "type": "array", "items": { "type": "string" } },
            "output_type": { "type": "string", "enum": ["text", "file"] },
            "runtimes": { "type": "array", "items": { "type": "string" } },
            "runtime_dependencies": { "type": "array", "items": { "type": "string" } },
            "runtime_env_vars": { "type": "array", "items": { "type": "string" } },
            "tool_list": { "type": "array", "items": { "type": "string" } },
            "workflow_yamls": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "workflow_id": { "type": "string" },
                  "content": { "type": "string" }
                },
                "required": ["workflow_id", "content"]
              }
            },
            "scripts": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "path": { "type": "string" },
                  "content": { "type": "string" }
                },
                "required": ["path", "content"]
              }
            },
            "references": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "path": { "type": "string" },
                  "content": { "type": "string" }
                },
                "required": ["path", "content"]
              }
            },
            "assets": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "path": { "type": "string" },
                  "content": { "type": "string" }
                },
                "required": ["path", "content"]
              }
            }
          },
          "required": ["skill_id", "name", "description", "version", "category", "instructions_markdown"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return BuildResult("error", "No NyxID access token available. User must be authenticated.");

        var (skillId, packageArgumentsJson, skillIdDiagnostics) = ExtractSkillIdAndPackageArguments(argumentsJson);
        if (skillId == null || packageArgumentsJson == null)
            return BuildDiagnosticsResult("validation_error", skillIdDiagnostics);

        var (request, parseDiagnostics) = OrnnSkillPublishRequestParser.Parse(packageArgumentsJson);
        if (request == null)
            return BuildDiagnosticsResult("validation_error", parseDiagnostics);

        var localValidation = await _validationPipeline.ValidateAsync(request, ct);
        if (!localValidation.IsValid)
            return BuildDiagnosticsResult("validation_error", localValidation.Diagnostics);

        var (package, buildValidation) = _packageBuilder.Build(request);
        if (package == null)
            return BuildDiagnosticsResult("validation_error", buildValidation.Diagnostics);

        var formatValidation = await _formatValidator.ValidateAsync(token, package.ZipBytes, ct);
        if (!formatValidation.IsValid)
        {
            return JsonSerializer.Serialize(new
            {
                result_type = "ornn_update_skill",
                status = "format_validation_error",
                error = formatValidation.Error,
                violations = formatValidation.Violations,
            });
        }

        var update = await _client.UpdateSkillAsync(token, skillId, package.ZipBytes, ct);
        if (!update.Succeeded)
            return BuildResult("error", update.Error ?? "Ornn update failed.");

        var updated = ExtractUpdatedSkill(update.RawResponse);
        return JsonSerializer.Serialize(new
        {
            result_type = "ornn_update_skill",
            status = "success",
            skill_id = skillId,
            name = request.Name,
            guid = updated.Guid ?? skillId,
            version = updated.Version ?? request.Version,
            skillHash = updated.SkillHash,
            package_bytes = package.ZipBytes.Length,
            response = update.RawResponse,
        });
    }

    private static (string? SkillId, string? PackageArgumentsJson, IReadOnlyList<OrnnSkillPublishDiagnostic> Diagnostics)
        ExtractSkillIdAndPackageArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return (null, null, [new OrnnSkillPublishDiagnostic("invalid_json", "Arguments JSON is required.")]);

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, [new OrnnSkillPublishDiagnostic("invalid_json", "Arguments must be a JSON object.")]);

            var diagnostics = new List<OrnnSkillPublishDiagnostic>();
            var skillId = ReadRequiredSkillId(root, diagnostics);
            if (diagnostics.Count > 0)
                return (null, null, diagnostics);

            return (skillId, StripSkillId(root), diagnostics);
        }
        catch (JsonException ex)
        {
            return (null, null, [new OrnnSkillPublishDiagnostic("invalid_json", ex.Message)]);
        }
    }

    private static string? ReadRequiredSkillId(JsonElement root, List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("skill_id", out var property))
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_field", "skill_id is required.", "$.skill_id"));
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_field", "skill_id must be a string.", "$.skill_id"));
            return null;
        }

        var value = property.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("missing_field", "skill_id must not be empty.", "$.skill_id"));
            return null;
        }

        if (!Guid.TryParse(value, out _))
        {
            diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_skill_id", "skill_id must be a GUID.", "$.skill_id"));
            return null;
        }

        return value;
    }

    private static string StripSkillId(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "skill_id", StringComparison.Ordinal))
                    continue;

                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildDiagnosticsResult(
        string status,
        IReadOnlyList<OrnnSkillPublishDiagnostic> diagnostics) =>
        JsonSerializer.Serialize(new
        {
            result_type = "ornn_update_skill",
            status,
            diagnostics,
        });

    private static string BuildResult(string status, string error) =>
        JsonSerializer.Serialize(new
        {
            result_type = "ornn_update_skill",
            status,
            error,
        });

    private static UpdatedSkillSubject ExtractUpdatedSkill(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new UpdatedSkillSubject(null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            return ExtractUpdatedSkill(doc.RootElement);
        }
        catch (JsonException)
        {
            return new UpdatedSkillSubject(null, null, null);
        }
    }

    private static UpdatedSkillSubject ExtractUpdatedSkill(JsonElement root)
    {
        var subject = ExtractUpdatedSkillFromObject(root);
        if (subject.HasAny)
            return subject;

        if (root.ValueKind != JsonValueKind.Object)
            return subject;

        foreach (var propertyName in new[] { "data", "result", "skill" })
        {
            if (!root.TryGetProperty(propertyName, out var nested) || nested.ValueKind != JsonValueKind.Object)
                continue;

            subject = ExtractUpdatedSkillFromObject(nested);
            if (subject.HasAny)
                return subject;
        }

        return subject;
    }

    private static UpdatedSkillSubject ExtractUpdatedSkillFromObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new UpdatedSkillSubject(null, null, null);

        return new UpdatedSkillSubject(
            TryGetString(element, "guid", "id", "skill_id", "skillId"),
            TryGetString(element, "version", "subject_version", "subjectVersion"),
            TryGetString(element, "skillHash", "skill_hash", "hash", "subject_hash"));
    }

    private static string? TryGetString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return value.ToString();
        }

        return null;
    }

    private sealed record UpdatedSkillSubject(string? Guid, string? Version, string? SkillHash)
    {
        public bool HasAny =>
            !string.IsNullOrWhiteSpace(Guid) ||
            !string.IsNullOrWhiteSpace(Version) ||
            !string.IsNullOrWhiteSpace(SkillHash);
    }
}
