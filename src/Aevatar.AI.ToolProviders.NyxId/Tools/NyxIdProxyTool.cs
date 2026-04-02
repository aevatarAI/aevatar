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

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    /// <summary>
    /// Proxy tool can execute POST/PUT/PATCH/DELETE against external services,
    /// so it is potentially destructive. Combined with Auto approval mode,
    /// the ToolApprovalMiddleware will require approval before execution.
    /// </summary>
    public bool IsDestructive => true;

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
        return await _client.ProxyRequestAsync(token, slug, path, method, body, headers, ct);
    }
}
