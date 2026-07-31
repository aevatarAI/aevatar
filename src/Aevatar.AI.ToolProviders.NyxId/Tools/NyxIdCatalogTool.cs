using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to browse the NyxID service catalog.</summary>
public sealed class NyxIdCatalogTool : INyxIdBuiltInTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdCatalogTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_catalog";

    public string Description =>
        "Browse available service templates in the NyxID catalog. " +
        "Provide 'slug' to get details for a specific service, or omit to list all.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "slug": {
              "type": "string",
              "description": "Service slug to show details for (e.g. 'llm-openai'). Omit to list all."
            }
          }
        }
        """;

    public bool IsReadOnly => true;

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return null;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var error) &&
                error.ValueKind != JsonValueKind.False)
            {
                var status = root.TryGetProperty("status", out var statusValue) &&
                             statusValue.TryGetInt32(out var httpStatus)
                    ? httpStatus
                    : 0;
                var code = status > 0
                    ? $"NYXID_CATALOG_HTTP_{status}"
                    : "NYXID_CATALOG_FAILURE";
                const string message = "The NyxID catalog request failed.";
                return Receipt(
                    callId,
                    toolName,
                    AgentToolReceiptStatus.Error,
                    JsonSerializer.Serialize(new { error = code, message }),
                    code,
                    message);
            }

            var requestedSlug = ToolArgs.Parse(argumentsJson).Str("slug");
            var verified = string.IsNullOrWhiteSpace(requestedSlug)
                ? root.ValueKind == JsonValueKind.Array ||
                  root.ValueKind == JsonValueKind.Object &&
                  root.TryGetProperty("entries", out var entries) &&
                  entries.ValueKind == JsonValueKind.Array
                : root.ValueKind == JsonValueKind.Object &&
                  root.TryGetProperty("slug", out var slug) &&
                  slug.ValueKind == JsonValueKind.String &&
                  string.Equals(slug.GetString(), requestedSlug, StringComparison.OrdinalIgnoreCase);
            return verified
                ? Receipt(callId, toolName, AgentToolReceiptStatus.Success, resultJson)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        var slug = args.Str("slug");

        if (!string.IsNullOrWhiteSpace(slug))
            return await _client.GetCatalogEntryAsync(token, slug, ct);

        return await _client.ListCatalogAsync(token, ct);
    }

    private AgentToolReceipt Receipt(
        string callId,
        string toolName,
        AgentToolReceiptStatus status,
        string resultJson,
        string errorCode = "",
        string errorMessage = "") =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = status,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ResultJson = resultJson ?? string.Empty,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
        };
}
