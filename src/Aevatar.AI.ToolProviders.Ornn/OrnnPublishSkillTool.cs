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

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        var success = CreateSuccessReceipt(callId, toolName, resultJson);
        if (success is not null)
            return success;

        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetNonEmptyString(root, out var resultType, "result_type") ||
                !string.Equals(resultType, "ornn_publish_skill", StringComparison.Ordinal) ||
                !TryGetNonEmptyString(root, out var status, "status"))
            {
                return null;
            }

            return status switch
            {
                "validation_error" => CreateValidationErrorReceipt(callId, toolName, resultJson, root),
                "format_validation_error" => CreateFormatValidationErrorReceipt(
                    callId,
                    toolName,
                    resultJson,
                    root),
                "error" => CreateMutationErrorReceipt(callId, toolName, resultJson, root),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
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
        {
            return OrnnSkillMutationFailureProtocol.Serialize(
                "ornn_publish_skill",
                new OrnnSkillMutationFailure(
                    OrnnSkillMutationFailureKind.Rejected,
                    "nyxid_access_token_missing",
                    "No NyxID access token available. User must be authenticated.",
                    null,
                    AgentToolFailureOutcome.CalleeConfirmed));
        }

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
            return OrnnSkillMutationFailureProtocol.Serialize("ornn_publish_skill", publish.Failure!);

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

    private AgentToolReceipt? CreateValidationErrorReceipt(
        string callId,
        string toolName,
        string resultJson,
        JsonElement root)
    {
        if (!root.TryGetProperty("diagnostics", out var diagnostics) ||
            diagnostics.ValueKind != JsonValueKind.Array ||
            diagnostics.GetArrayLength() == 0)
        {
            return null;
        }

        var diagnostic = diagnostics[0];
        if (diagnostic.ValueKind != JsonValueKind.Object ||
            !TryGetNonEmptyString(diagnostic, out var code, "code", "Code") ||
            !TryGetNonEmptyString(diagnostic, out var message, "message", "Message"))
        {
            return null;
        }

        var safeMessage = $"{code}: {message}";
        if (TryGetNonEmptyString(diagnostic, out var path, "path", "Path"))
            safeMessage += $" (path: {path})";

        return CreateErrorReceipt(callId, toolName, resultJson, code, safeMessage);
    }

    private AgentToolReceipt? CreateFormatValidationErrorReceipt(
        string callId,
        string toolName,
        string resultJson,
        JsonElement root)
    {
        const string errorCode = "ornn_format_validation_error";
        var details = new List<string>();
        if (TryGetNonEmptyString(root, out var error, "error"))
            details.Add(error);

        if (root.TryGetProperty("violations", out var violations) &&
            violations.ValueKind == JsonValueKind.Array)
        {
            foreach (var violation in violations.EnumerateArray())
            {
                if (violation.ValueKind != JsonValueKind.Object)
                    continue;

                var hasRule = TryGetNonEmptyString(violation, out var rule, "rule", "Rule");
                var hasMessage = TryGetNonEmptyString(violation, out var message, "message", "Message");
                if (!hasRule && !hasMessage)
                    continue;

                details.Add(hasRule && hasMessage ? $"{rule}: {message}" : hasRule ? rule : message);
            }
        }

        return details.Count == 0
            ? null
            : CreateErrorReceipt(
                callId,
                toolName,
                resultJson,
                errorCode,
                $"{errorCode}: {string.Join("; ", details)}");
    }

    private AgentToolReceipt? CreateMutationErrorReceipt(
        string callId,
        string toolName,
        string resultJson,
        JsonElement root)
    {
        if (!OrnnSkillMutationFailureProtocol.TryParse(root, out var failure))
            return null;

        return CreateErrorReceipt(
            callId,
            toolName,
            resultJson,
            failure.Code,
            $"{failure.Code}: {failure.Message}",
            failure.Outcome);
    }

    private AgentToolReceipt CreateErrorReceipt(
        string callId,
        string toolName,
        string resultJson,
        string errorCode,
        string errorMessage,
        AgentToolFailureOutcome failureOutcome = AgentToolFailureOutcome.CalleeConfirmed) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.Auto,
            IsDestructive = false,
            SideEffectKind = SideEffectKind,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ResultJson = resultJson ?? string.Empty,
            FailureOutcome = failureOutcome,
        };

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

    private static bool TryGetNonEmptyString(
        JsonElement element,
        out string value,
        params string[] keys)
    {
        value = string.Empty;
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String)
                continue;

            value = property.GetString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
                return true;
        }

        value = string.Empty;
        return false;
    }

    private sealed record PublishedSkillSubject(string? Guid, string? Version, string? SkillHash)
    {
        public bool HasAny =>
            !string.IsNullOrWhiteSpace(Guid) ||
            !string.IsNullOrWhiteSpace(Version) ||
            !string.IsNullOrWhiteSpace(SkillHash);
    }
}
