using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to browse the NyxID service catalog.</summary>
public sealed class NyxIdCatalogTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdCatalogTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_catalog";

    public string Description =>
        "Browse available service templates in the NyxID catalog. " +
        "Use 'list' to see all available services, or 'show' with a slug to get details about a specific service.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show"],
              "description": "Action to perform: 'list' all catalog entries or 'show' details for a specific slug"
            },
            "slug": {
              "type": "string",
              "description": "Service slug (required when action is 'show')"
            }
          },
          "required": ["action"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string action = "list";
        string? slug = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("slug", out var s))
                slug = s.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "show" when !string.IsNullOrWhiteSpace(slug) =>
                await _client.GetCatalogEntryAsync(token, slug, ct),
            _ => await _client.ListCatalogAsync(token, ct),
        };
    }
}
