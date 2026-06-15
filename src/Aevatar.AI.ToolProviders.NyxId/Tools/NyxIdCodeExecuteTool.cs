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

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

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
        var token = AgentToolRequestContext.NyxIdAccessToken;
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

        // Current chrono-sandbox-service exposes /execute with body { language, script }.
        // Older sandbox builds expose /run with body { language, code }. We POST the modern
        // contract first; on a NyxID-proxy 404 (slug exists but upstream returned 404, which
        // indicates the path doesn't exist on that backend), retry the legacy contract so a
        // host still pinned to the old sandbox keeps working.
        var modernBody = JsonSerializer.Serialize(new { language = language, script = code });
        var modernResult = await _client.ProxyRequestAsync(token, slug, "/execute", "POST", modernBody, null, ct);
        if (!IsUpstream404(modernResult))
            return modernResult;

        _logger.LogInformation(
            "[code_execute] {Slug} returned 404 on /execute; retrying legacy /run contract", slug);
        var legacyBody = JsonSerializer.Serialize(new { language = language, code = code });
        return await _client.ProxyRequestAsync(token, slug, "/run", "POST", legacyBody, null, ct);
    }

    /// <summary>
    /// NyxID's proxy wraps non-2xx upstream responses as
    /// <c>{"error":true,"status":N,"body":"..."}</c>. A 404 here means "slug exists but the
    /// requested path doesn't" — the case where we should retry the legacy contract.
    /// Service-not-found / catalog-miss surfaces with a different shape and is left alone.
    /// </summary>
    private static bool IsUpstream404(string proxyResponse)
    {
        if (string.IsNullOrWhiteSpace(proxyResponse))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(proxyResponse);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("error", out var errProp) ||
                errProp.ValueKind != JsonValueKind.True)
            {
                return false;
            }
            return root.TryGetProperty("status", out var statusProp) &&
                   statusProp.ValueKind == JsonValueKind.Number &&
                   statusProp.GetInt32() == 404;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts sandbox slug from the connected services context injected by the endpoint middleware.
    /// </summary>
    private static string? ResolveSandboxSlugFromContext()
    {
        var context = AgentToolRequestContext.ConnectedServicesContext;
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "[code_execute] Failed to probe known sandbox slug: {Slug}", candidate);
            }
        }

        return null;
    }
}
