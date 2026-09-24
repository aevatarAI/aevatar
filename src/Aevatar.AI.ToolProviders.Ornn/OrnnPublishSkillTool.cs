using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Ornn.Publishing;

namespace Aevatar.AI.ToolProviders.Ornn;

public sealed class OrnnPublishSkillTool : IAgentTool
{
    private readonly OrnnSkillPublishingService _publishingService;

    public OrnnPublishSkillTool(OrnnSkillPublishingService publishingService)
    {
        _publishingService = publishingService;
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
        var published = OrnnSkillPublishingService.ExtractPublishedSkill(resultJson);
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

        var result = await _publishingService.PublishAsync(token, request, ct);
        return FormatResult(request, result);
    }

    private static string FormatResult(
        OrnnSkillPublishRequest request,
        OrnnSkillPublishingResult result)
    {
        if (result.Diagnostics.Count > 0)
            return BuildDiagnosticsResult(result.Status, result.Diagnostics);

        if (result.FormatViolations.Count > 0 || string.Equals(result.Status, "format_validation_error", StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(new
            {
                result_type = "ornn_publish_skill",
                status = result.Status,
                error = result.Error,
                violations = result.FormatViolations,
            });
        }

        if (!result.IsSuccess)
            return BuildResult(result.Status, result.Error ?? "Ornn publish failed.");

        return JsonSerializer.Serialize(new
        {
            result_type = "ornn_publish_skill",
            status = result.Status,
            skill_name = request.Name,
            guid = result.Guid,
            version = result.Version ?? request.Version,
            skillHash = result.SkillHash,
            package_bytes = result.PackageBytes,
            response = result.RawResponse,
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
}
