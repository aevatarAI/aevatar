using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to make proxied requests to downstream services through NyxID.</summary>
public sealed class NyxIdProxyTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdProxyTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_proxy";

    public string Description =>
        "Make HTTP requests to downstream services through NyxID's credential-injecting proxy. " +
        "NyxID automatically injects the user's stored credentials. " +
        "Use nyxid_services first to discover available service slugs. " +
        "Paths are relative to the service's base URL.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "slug": {
              "type": "string",
              "description": "Service slug (e.g. 'llm-openai', 'api-github')"
            },
            "path": {
              "type": "string",
              "description": "API path relative to the service's base URL (e.g. '/chat/completions', '/user/repos')"
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
          "required": ["slug", "path"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string? slug = null;
        string? path = null;
        string method = "GET";
        string? body = null;
        Dictionary<string, string>? headers = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
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
        catch { /* use defaults */ }

        if (string.IsNullOrWhiteSpace(slug))
            return "Error: 'slug' is required.";
        if (string.IsNullOrWhiteSpace(path))
            return "Error: 'path' is required.";

        return await _client.ProxyRequestAsync(token, slug, path, method, body, headers, ct);
    }
}
