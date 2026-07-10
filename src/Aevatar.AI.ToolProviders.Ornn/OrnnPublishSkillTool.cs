using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Ornn.Publishing;

namespace Aevatar.AI.ToolProviders.Ornn;

public sealed class OrnnPublishSkillTool : IAgentTool
{
    private readonly OrnnSkillPublishValidationPipeline _validationPipeline;
    private readonly OrnnSkillPackageBuilder _packageBuilder;
    private readonly OrnnSkillPackageFormatValidator _formatValidator;
    private readonly OrnnSkillClient _client;

    public OrnnPublishSkillTool(
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

    public string Name => "ornn_publish_skill";

    public string Description =>
        "Publish a new private Ornn skill package for the current NyxID caller. " +
        "Private publishing executes directly and does not require NyxID approval. " +
        "Use only after building a complete skill from typed fields; this validates workflow template YAML, scripts, package format, then uploads the ZIP. " +
        "Workflow YAMLs published here are Ornn package templates/import sources, not Scope Workflow runtime publication. " +
        "The tool never accepts credentials, service routing fields, raw file maps, metadata bags, public visibility, or skip-validation flags.";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public string SideEffectKind => "ornn.publish.skill";

    public AgentToolReceipt? CreateSuccessReceipt(string callId, string toolName, string resultJson)
    {
        var published = ExtractPublishedSkill(resultJson);
        if (!published.HasAny)
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
            SubjectId = published.Guid ?? string.Empty,
            SubjectVersion = published.Version ?? string.Empty,
            SubjectHash = published.SkillHash ?? string.Empty,
            ResultJson = resultJson ?? string.Empty,
        };
    }

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
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
          "required": ["name", "description", "version", "category", "instructions_markdown"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return BuildResult("error", "No NyxID access token available. User must be authenticated.");

        var (request, parseDiagnostics) = OrnnSkillPublishRequestParser.Parse(argumentsJson);
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
                result_type = "ornn_publish_skill",
                status = "format_validation_error",
                error = formatValidation.Error,
                violations = formatValidation.Violations,
            });
        }

        var publish = await _client.PublishSkillAsync(token, package.ZipBytes, ct);
        if (!publish.Succeeded)
            return BuildResult("error", publish.Error ?? "Ornn publish failed.");

        var published = ExtractPublishedSkill(publish.RawResponse);
        return JsonSerializer.Serialize(new
        {
            result_type = "ornn_publish_skill",
            status = "success",
            skill_name = request.Name,
            guid = published.Guid,
            version = published.Version ?? request.Version,
            skillHash = published.SkillHash,
            package_bytes = package.ZipBytes.Length,
            response = publish.RawResponse,
        });
    }

    private static string BuildDiagnosticsResult(
        string status,
        IReadOnlyList<OrnnSkillPublishDiagnostic> diagnostics) =>
        JsonSerializer.Serialize(new
        {
            result_type = "ornn_publish_skill",
            status,
            diagnostics,
        });

    private static string BuildResult(string status, string error) =>
        JsonSerializer.Serialize(new
        {
            result_type = "ornn_publish_skill",
            status,
            error,
        });

    private static PublishedSkillSubject ExtractPublishedSkill(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new PublishedSkillSubject(null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            return ExtractPublishedSkill(doc.RootElement);
        }
        catch (JsonException)
        {
            return new PublishedSkillSubject(null, null, null);
        }
    }

    private static PublishedSkillSubject ExtractPublishedSkill(JsonElement root)
    {
        var subject = ExtractPublishedSkillFromObject(root);
        if (subject.HasAny)
            return subject;

        if (root.ValueKind != JsonValueKind.Object)
            return subject;

        foreach (var propertyName in new[] { "data", "result", "skill" })
        {
            if (!root.TryGetProperty(propertyName, out var nested) || nested.ValueKind != JsonValueKind.Object)
                continue;

            subject = ExtractPublishedSkillFromObject(nested);
            if (subject.HasAny)
                return subject;
        }

        return subject;
    }

    private static PublishedSkillSubject ExtractPublishedSkillFromObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new PublishedSkillSubject(null, null, null);

        return new PublishedSkillSubject(
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

    private sealed record PublishedSkillSubject(string? Guid, string? Version, string? SkillHash)
    {
        public bool HasAny =>
            !string.IsNullOrWhiteSpace(Guid) ||
            !string.IsNullOrWhiteSpace(Version) ||
            !string.IsNullOrWhiteSpace(SkillHash);
    }
}
