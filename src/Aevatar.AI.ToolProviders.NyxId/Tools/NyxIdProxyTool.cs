using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to make proxied requests to downstream services through NyxID.</summary>
public sealed class NyxIdProxyTool : IAgentTool
{
    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;

    public NyxIdProxyTool(NyxIdApiClient client, ILogger? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name => "nyxid_proxy";

    public string Description =>
        "Make HTTP requests to downstream services through NyxID's credential-injecting proxy. " +
        "NyxID automatically injects the user's stored credentials. " +
        "Omit slug to discover all proxyable services. " +
        "Provide slug + path to send a proxied request.";

    /// <summary>
    /// No Aevatar-side approval needed. NyxID's proxy layer handles approval
    /// enforcement server-side: when a service has approval enabled, NyxID
    /// blocks the proxy request, sends a push notification (Telegram/FCM/APNs),
    /// and waits for the user to approve before completing the request.
    /// The proxy response may take 30+ seconds during approval wait.
    /// </summary>
    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "slug": {
              "type": "string",
              "description": "Service slug (e.g. 'llm-openai', 'api-github'). Omit to list all proxyable services."
            },
            "path": {
              "type": "string",
              "description": "API path relative to the service's base URL (e.g. '/chat/completions', '/getMe')"
            },
            "method": {
              "type": "string",
              "enum": ["GET", "POST", "PUT", "PATCH", "DELETE"],
              "description": "HTTP method (default: GET)"
            },
            "body": {
              "type": "string",
              "description": "Request body as JSON string (for POST/PUT/PATCH)"
            },
            "headers": {
              "type": "object",
              "additionalProperties": { "type": "string" },
              "description": "Additional HTTP headers"
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        var orgToken = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdOrgToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        _logger.LogDebug("[nyxid_proxy] Raw arguments: {Args}", argumentsJson);

        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
        {
            _logger.LogWarning("[nyxid_proxy] Argument parse failed: {Error}, raw={Raw}", args.ParseError, args.Raw);
            return $"{{\"error\":\"Failed to parse tool arguments\",\"detail\":{System.Text.Json.JsonSerializer.Serialize(args.ParseError)},\"received\":{System.Text.Json.JsonSerializer.Serialize(args.Raw)}}}";
        }

        var slug = args.Str("slug") ?? args.Str("service");
        var path = args.Str("path");
        var method = args.Str("method", "GET");
        var body = args.RawOrStr("body");
        var headers = args.Headers();

        // No slug → discover mode: merge services from both tokens
        if (string.IsNullOrWhiteSpace(slug))
        {
            _logger.LogInformation("[nyxid_proxy] No slug provided, returning service discovery. raw={Raw}", args.Raw);
            return await DiscoverMergedServicesAsync(token, orgToken, ct);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("[nyxid_proxy] Missing path. slug={Slug}, raw={Raw}", slug, args.Raw);
            return $"{{\"error\":\"'path' is required when 'slug' is provided\",\"received\":{args.Raw}}}";
        }

        // Resolve which token owns the target service: user token first, fallback to org token
        var effectiveToken = await ResolveTokenForServiceAsync(token, orgToken, slug, ct);

        _logger.LogInformation("[nyxid_proxy] {Method} slug={Slug} path={Path} tokenSource={Source}",
            method, slug, path, effectiveToken == token ? "user" : "org");
        var result = await _client.ProxyRequestAsync(effectiveToken, slug, path, method, body, headers, ct);

        // Detect NyxID approval-related responses and provide actionable context to the LLM.
        // NyxID proxy blocks the request during approval wait (up to 30s). If the user
        // doesn't approve in time, NyxID returns an error with code 7000 or 7001.
        if (IsApprovalError(result, out var approvalCode, out var approvalRequestId))
        {
            _logger.LogInformation(
                "[nyxid_proxy] Approval response: code={Code} requestId={RequestId}",
                approvalCode, approvalRequestId);
        }

        return result;
    }

    /// <summary>
    /// Discover services from both user and org tokens, merge into a single list.
    /// Services from user token are marked as "personal", org token as "org".
    /// Deduplicates by slug (user takes precedence).
    /// </summary>
    private async Task<string> DiscoverMergedServicesAsync(
        string userToken, string? orgToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orgToken) || orgToken == userToken)
            return await _client.DiscoverProxyServicesAsync(userToken, ct);

        var userServicesJson = await _client.DiscoverProxyServicesAsync(userToken, ct);
        var orgServicesJson = await _client.DiscoverProxyServicesAsync(orgToken, ct);

        // Parse and merge: user services take precedence on slug collision
        try
        {
            using var userDoc = System.Text.Json.JsonDocument.Parse(userServicesJson);
            using var orgDoc = System.Text.Json.JsonDocument.Parse(orgServicesJson);

            var userSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<System.Text.Json.JsonElement>();

            // Collect user services
            if (userDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var svc in userDoc.RootElement.EnumerateArray())
                {
                    merged.Add(svc);
                    if (svc.TryGetProperty("slug", out var slugProp))
                        userSlugs.Add(slugProp.GetString() ?? string.Empty);
                }
            }

            // Add org services that don't collide with user services
            if (orgDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var svc in orgDoc.RootElement.EnumerateArray())
                {
                    if (svc.TryGetProperty("slug", out var slugProp))
                    {
                        var s = slugProp.GetString() ?? string.Empty;
                        if (!userSlugs.Contains(s))
                            merged.Add(svc);
                    }
                }
            }

            return System.Text.Json.JsonSerializer.Serialize(merged);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "[nyxid_proxy] Failed to merge service lists, returning user services only");
            return userServicesJson;
        }
    }

    /// <summary>
    /// Resolve which token to use for a given service slug.
    /// Checks user token's service list first; falls back to org token.
    /// </summary>
    private async Task<string> ResolveTokenForServiceAsync(
        string userToken, string? orgToken, string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orgToken) || orgToken == userToken)
            return userToken;

        // Check if the user token can see this service
        if (await ServiceExistsForTokenAsync(userToken, slug, ct))
            return userToken;

        // Fallback to org token
        if (await ServiceExistsForTokenAsync(orgToken, slug, ct))
        {
            _logger.LogInformation(
                "[nyxid_proxy] Service {Slug} not found for user token, using org token", slug);
            return orgToken;
        }

        // Neither has it — use user token and let NyxID return the error
        return userToken;
    }

    /// <summary>
    /// Check whether a given token can access a service by slug.
    /// Uses the service discovery endpoint and checks the result.
    /// </summary>
    private async Task<bool> ServiceExistsForTokenAsync(
        string token, string slug, CancellationToken ct)
    {
        try
        {
            var servicesJson = await _client.DiscoverProxyServicesAsync(token, ct);
            using var doc = System.Text.Json.JsonDocument.Parse(servicesJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return false;

            foreach (var svc in doc.RootElement.EnumerateArray())
            {
                if (svc.TryGetProperty("slug", out var slugProp) &&
                    string.Equals(slugProp.GetString(), slug, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Detect NyxID approval error codes (7000 = approval_required, 7001 = approval_failed).
    /// </summary>
    private static bool IsApprovalError(string result, out int code, out string? requestId)
    {
        code = 0;
        requestId = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number)
                code = c.GetInt32();
            if (doc.RootElement.TryGetProperty("approval_request_id", out var rid))
                requestId = rid.GetString();
            return code is 7000 or 7001;
        }
        catch
        {
            return false;
        }
    }
}
