using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// First-class code execution tool. Wraps the chrono-sandbox proxy service
/// with a clean interface so the agent can run code without needing to
/// discover services or guess API paths.
/// </summary>
public sealed class NyxIdCodeExecuteTool : IAgentTool
{
    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;

    public NyxIdCodeExecuteTool(NyxIdApiClient client, ILogger? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name => "code_execute";

    public string Description =>
        "Execute code in a sandboxed environment. " +
        "Supports Python, JavaScript, TypeScript, and Bash. " +
        "Returns stdout, stderr, and exit code.";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    /// <summary>Code execution is always potentially destructive.</summary>
    public bool? RequiresApproval(string argumentsJson) => true;

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "language": {
              "type": "string",
              "enum": ["python", "javascript", "typescript", "bash"],
              "description": "Programming language to execute"
            },
            "code": {
              "type": "string",
              "description": "Code to execute"
            }
          },
          "required": ["language", "code"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        var language = args.Str("language");
        var code = args.Str("code");

        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(code))
            return """{"error":"Both 'language' and 'code' are required."}""";

        // Resolve sandbox slug: context → API discovery → known slugs → give up
        var slug = ResolveSandboxSlugFromContext()
                   ?? await DiscoverSandboxSlugAsync(token, ct);

        // Last resort: try well-known sandbox slugs directly
        if (string.IsNullOrWhiteSpace(slug))
            slug = await ProbeKnownSandboxSlugsAsync(token, ct);

        if (string.IsNullOrWhiteSpace(slug))
        {
            return """{"error":"No sandbox service connected. Use nyxid_catalog to browse available sandbox services, then connect one with nyxid_services."}""";
        }

        _logger.LogInformation("[code_execute] {Language} via slug={Slug}", language, slug);

        var body = JsonSerializer.Serialize(new { language, code });
        var result = await _client.ProxyRequestAsync(token, slug, "/run", "POST", body, null, ct);
        return result;
    }

    /// <summary>
    /// Extracts sandbox slug from the connected services context injected by the endpoint middleware.
    /// </summary>
    private static string? ResolveSandboxSlugFromContext()
    {
        var context = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.ConnectedServicesContext);
        if (string.IsNullOrWhiteSpace(context))
            return null;

        // Parse the connected services context to find sandbox slug.
        // The context contains lines like: "- **name** (slug: `chrono-sandbox-service`)"
        foreach (var line in context.Split('\n'))
        {
            if (!line.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
                continue;

            var slugStart = line.IndexOf("slug: `", StringComparison.Ordinal);
            if (slugStart < 0) continue;
            slugStart += "slug: `".Length;
            var slugEnd = line.IndexOf('`', slugStart);
            if (slugEnd <= slugStart) continue;

            return line[slugStart..slugEnd];
        }

        return null;
    }

    /// <summary>
    /// Fallback: call DiscoverProxyServices API to find a sandbox service.
    /// Used when the connected services context is missing or doesn't contain a sandbox entry.
    /// </summary>
    private async Task<string?> DiscoverSandboxSlugAsync(string token, CancellationToken ct)
    {
        try
        {
            var json = await _client.DiscoverProxyServicesAsync(token, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement items = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("services", out var svc)) items = svc;
                else if (root.TryGetProperty("data", out var data)) items = data;
            }

            if (items.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in items.EnumerateArray())
            {
                var slug = item.TryGetProperty("slug", out var s) ? s.GetString() : null;
                if (!string.IsNullOrWhiteSpace(slug) &&
                    slug.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[code_execute] Discovered sandbox slug via fallback: {Slug}", slug);
                    return slug;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[code_execute] Fallback sandbox discovery failed");
        }

        return null;
    }

    /// <summary>
    /// Last resort: try well-known sandbox slugs with a lightweight probe request.
    /// If the proxy returns a non-error response, the slug is valid.
    /// </summary>
    private static readonly string[] KnownSandboxSlugs =
        ["chrono-sandbox-service", "chrono-sandbox", "sandbox"];

    private async Task<string?> ProbeKnownSandboxSlugsAsync(string token, CancellationToken ct)
    {
        foreach (var candidate in KnownSandboxSlugs)
        {
            try
            {
                // Probe with a minimal request — just check if the slug is routable.
                // NyxID proxy returns {"error": true, "status": 404} when the slug doesn't exist.
                // Any other response (even upstream 4xx/5xx) means the slug is valid.
                var response = await _client.ProxyRequestAsync(
                    token, candidate, "/health", "GET", null, null, ct);

                // Check for NyxID-level "slug not found" error
                if (response.Contains("\"error\"") &&
                    (response.Contains("\"status\": 404") || response.Contains("\"status\":404")))
                {
                    continue; // This slug doesn't exist in NyxID
                }

                // Check for connection-level errors (the response from SendAsync catch block)
                if (response.Contains("\"error\": true") && response.Contains("\"message\""))
                {
                    continue; // Network error, can't determine if slug exists
                }

                _logger.LogInformation("[code_execute] Probed known sandbox slug: {Slug}", candidate);
                return candidate;
            }
            catch
            {
                // Network/timeout error — try next candidate
            }
        }

        return null;
    }
}
