using System.Text.Json;
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
        "Use only after building a complete skill from typed fields; this validates workflow YAML, scripts, package format, then uploads the ZIP. " +
        "The tool never accepts credentials, service routing fields, raw file maps, metadata bags, public visibility, or skip-validation flags.";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

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

        return JsonSerializer.Serialize(new
        {
            result_type = "ornn_publish_skill",
            status = "success",
            skill_name = request.Name,
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
}
