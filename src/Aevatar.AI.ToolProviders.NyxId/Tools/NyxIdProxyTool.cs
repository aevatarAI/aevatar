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

        // No slug → discover mode
        if (string.IsNullOrWhiteSpace(slug))
        {
            _logger.LogInformation("[nyxid_proxy] No slug provided, returning service discovery. raw={Raw}", args.Raw);
            return await _client.DiscoverProxyServicesAsync(token, ct);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("[nyxid_proxy] Missing path. slug={Slug}, raw={Raw}", slug, args.Raw);
            return $"{{\"error\":\"'path' is required when 'slug' is provided\",\"received\":{args.Raw}}}";
        }

        _logger.LogInformation("[nyxid_proxy] {Method} slug={Slug} path={Path}", method, slug, path);
        var result = await _client.ProxyRequestAsync(token, slug, path, method, body, headers, ct);

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
