using System.Text.Json;
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
        "Use 'discover' to list all proxyable services with their proxy URLs, " +
        "or provide 'slug' and 'path' to send a proxied request.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["request", "discover"],
              "description": "Action: 'request' (default) to send a proxied request, or 'discover' to list proxyable services"
            },
            "slug": {
              "type": "string",
              "description": "Service slug (e.g. 'llm-openai', 'api-github'). Required for 'request'."
            },
            "path": {
              "type": "string",
              "description": "API path relative to the service's base URL (e.g. '/chat/completions'). Required for 'request'."
            },
            "method": {
              "type": "string",
              "enum": ["GET", "POST", "PUT", "PATCH", "DELETE"],
              "description": "HTTP method (default: GET)"
            },
            "body": {
              "type": "string",
              "description": "Request body (JSON string, for POST/PUT/PATCH)"
            },
            "headers": {
              "type": "object",
              "additionalProperties": { "type": "string" },
              "description": "Additional HTTP headers"
            }
          },
          "required": []
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        _logger.LogInformation("[nyxid_proxy] Raw arguments: {Args}", argumentsJson);

        string action = "request";
        string? slug = null;
        string? path = null;
        string method = "GET";
        string? body = null;
        Dictionary<string, string>? headers = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "request";
            if (doc.RootElement.TryGetProperty("slug", out var s))
                slug = s.GetString();
            if (doc.RootElement.TryGetProperty("path", out var p))
                path = p.GetString();
            if (doc.RootElement.TryGetProperty("method", out var m))
                method = m.GetString() ?? "GET";
            if (doc.RootElement.TryGetProperty("body", out var b))
                body = b.ValueKind == JsonValueKind.String ? b.GetString() : b.GetRawText();
            if (doc.RootElement.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                headers = new Dictionary<string, string>();
                foreach (var prop in h.EnumerateObject())
                    headers[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[nyxid_proxy] Failed to parse arguments: {Args}", argumentsJson);
            return $"Error: Failed to parse arguments. Raw input: {argumentsJson}";
        }

        if (action == "discover")
            return await _client.DiscoverProxyServicesAsync(token, ct);

        _logger.LogInformation("[nyxid_proxy] Parsed: slug={Slug}, path={Path}, method={Method}", slug, path, method);

        if (string.IsNullOrWhiteSpace(slug))
            return $"Error: 'slug' is required for request action. Received arguments: {argumentsJson}";
        if (string.IsNullOrWhiteSpace(path))
            return $"Error: 'path' is required for request action. Received arguments: {argumentsJson}";

        return await _client.ProxyRequestAsync(token, slug, path, method, body, headers, ct);
    }
}
