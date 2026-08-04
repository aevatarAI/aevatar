using System.Text.Json;
using Aevatar.AI.Abstractions;
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
public sealed class NyxIdCodeExecuteTool : INyxIdBuiltInTool
{
    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;
    private readonly string? _sandboxServiceSlug;

    public NyxIdCodeExecuteTool(
        NyxIdApiClient client,
        ILogger? logger = null,
        string? sandboxServiceSlug = NyxIdToolOptions.DefaultSandboxServiceSlug)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
        _sandboxServiceSlug = string.IsNullOrWhiteSpace(sandboxServiceSlug)
            ? null
            : sandboxServiceSlug.Trim();
    }

    public string Name => "code_execute";

    public string Description =>
        "Execute code in a sandboxed environment. " +
        "Supports Python, JavaScript, TypeScript, and Bash. " +
        "Returns stdout, stderr, and exit code.";

    // Approval intentionally not required (by design): code runs entirely in the
    // remote, isolated chrono-sandbox service (see class summary) — never on this
    // host — and only { language, script } is forwarded, so no caller token,
    // secrets, or env enter the sandbox runtime. The sandbox is the isolation
    // boundary, so a host-side approval gate adds nothing here. Contrast
    // NyxIdSshExecTool, which targets a real host and so keeps ApprovalMode.Auto.
    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        var proxyReceipt = NyxIdProxyReceiptFactory.TryCreate(
            callId,
            toolName,
            _sandboxServiceSlug ?? NyxIdToolOptions.DefaultSandboxServiceSlug,
            userServiceId: null,
            serviceLabel: null,
            resourceUri: "/execute",
            resultJson);
        if (proxyReceipt != null)
            return proxyReceipt;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var hasError = root.TryGetProperty("error", out _);
            var exitCodeValue = 0;
            var hasExitCode = root.TryGetProperty("exit_code", out var exitCode) &&
                              exitCode.TryGetInt32(out exitCodeValue);
            var nonZeroExit = hasExitCode && exitCodeValue != 0;
            if (hasError || nonZeroExit)
            {
                const string errorCode = "CODE_EXECUTE_FAILED";
                const string errorMessage = "Code execution failed.";
                return new AgentToolReceipt
                {
                    CallId = callId ?? string.Empty,
                    ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
                    Status = AgentToolReceiptStatus.Error,
                    ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage,
                    ResultJson = "{\"error\":\"CODE_EXECUTE_FAILED\",\"message\":\"Code execution failed.\"}",
                };
            }

            if (!hasExitCode)
                return null;
        }
        catch (JsonException)
        {
            return null;
        }

        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
        };
    }

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

        // A connected-service selection wins when present. Otherwise use the
        // host-owned route that the OAuth binding requested as a resource.
        var slug = ResolveSandboxSlugFromContext()
                   ?? _sandboxServiceSlug
                   ?? await DiscoverSandboxSlugAsync(token, ct);

        if (string.IsNullOrWhiteSpace(slug))
        {
            return """{"error":"No sandbox service connected. Use nyxid_catalog to browse available sandbox services, then connect one with nyxid_services."}""";
        }

        _logger.LogInformation("[code_execute] {Language} via slug={Slug}", language, slug);

        // chrono-sandbox exposes /execute with body { language, script }.
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
        // The context contains lines like: "- **name** (slug: `chrono-sandbox`)"
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
    /// Fallback for hosts that explicitly leave the configured sandbox slug empty:
    /// call DiscoverProxyServices API to find a sandbox service.
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
}
