using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdProxyExecuteTool : IAgentTool
{
    private readonly NyxIdSpecCatalog _catalog;
    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;

    public NyxIdProxyExecuteTool(
        NyxIdSpecCatalog catalog,
        NyxIdApiClient client,
        ILogger<NyxIdProxyExecuteTool>? logger = null)
    {
        _catalog = catalog;
        _client = client;
        _logger = logger ?? NullLogger<NyxIdProxyExecuteTool>.Instance;
    }

    public string Name => "nyxid_proxy_execute";

    public string Description =>
        "Execute a NyxID API operation discovered via nyxid_search_capabilities. " +
        "Validates parameters against the cached OpenAPI spec before sending. " +
        "Use this for NyxID operations not covered by specialized tools.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "operation_id": {
              "type": "string",
              "description": "Operation ID from nyxid_search_capabilities results"
            },
            "path_params": {
              "type": "object",
              "description": "Path parameter substitutions, e.g. {\"org_id\": \"abc123\"}"
            },
            "query": {
              "type": "object",
              "description": "Query string parameters"
            },
            "body": {
              "description": "Request body (JSON object or null)"
            }
          },
          "required": ["operation_id"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        var operationId = args.Str("operation_id");

        if (string.IsNullOrWhiteSpace(operationId))
            return """{"error":"'operation_id' is required. Use nyxid_search_capabilities first to find the operation."}""";

        var op = _catalog.Operations.FirstOrDefault(
            o => string.Equals(o.OperationId, operationId, StringComparison.OrdinalIgnoreCase));

        if (op == null)
            return $"{{\"error\":\"Operation '{operationId}' not found in spec catalog. Use nyxid_search_capabilities to find valid operations.\"}}";

        var path = op.Path;
        var warnings = new List<string>();

        var pathParams = args.Element("path_params");
        if (pathParams.HasValue && pathParams.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var param in pathParams.Value.EnumerateObject())
            {
                var placeholder = $"{{{param.Name}}}";
                var value = param.Value.ValueKind == JsonValueKind.String
                    ? param.Value.GetString() ?? ""
                    : param.Value.GetRawText();
                path = path.Replace(placeholder, Uri.EscapeDataString(value));
            }
        }

        if (path.Contains('{'))
            warnings.Add($"Unresolved path parameters in '{path}'. Provide them via path_params.");

        var queryParams = args.Element("query");
        if (queryParams.HasValue && queryParams.Value.ValueKind == JsonValueKind.Object)
        {
            var qsParts = new List<string>();
            foreach (var param in queryParams.Value.EnumerateObject())
            {
                var value = param.Value.ValueKind == JsonValueKind.String
                    ? param.Value.GetString() ?? ""
                    : param.Value.GetRawText();
                qsParts.Add($"{Uri.EscapeDataString(param.Name)}={Uri.EscapeDataString(value)}");
            }
            if (qsParts.Count > 0)
                path = $"{path}?{string.Join('&', qsParts)}";
        }

        string? bodyJson = args.RawOrStr("body");

        if (op.RequestBodySchema != null && string.IsNullOrWhiteSpace(bodyJson) &&
            op.Method is "POST" or "PUT" or "PATCH")
            warnings.Add("This operation expects a request body but none was provided.");

        _logger.LogInformation(
            "NyxIdProxyExecute: {Method} {Path} (operation: {OperationId})",
            op.Method, path, operationId);

        string result;
        try
        {
            result = op.Method switch
            {
                "GET" => await _client.GetAsync(token, path, ct),
                "POST" => await _client.PostAsync(token, path, bodyJson ?? "{}", ct),
                "PUT" => await _client.PutAsync(token, path, bodyJson ?? "{}", ct),
                "PATCH" => await _client.PatchAsync(token, path, bodyJson ?? "{}", ct),
                "DELETE" => await _client.DeleteAsync(token, path, ct),
                _ => await _client.GetAsync(token, path, ct),
            };
        }
        catch (Exception ex)
        {
            return $"{{\"error\":\"{EscapeJson(ex.Message)}\"}}";
        }

        if (warnings.Count > 0)
        {
            var warningsJson = JsonSerializer.Serialize(warnings);
            return $"{{\"warnings\":{warningsJson},\"response\":{result}}}";
        }

        return result;
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
